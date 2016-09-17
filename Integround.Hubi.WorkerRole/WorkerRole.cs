using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.ServiceRuntime;
using System.Linq;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using Integround.Components.Core;
using Integround.Components.Http.HttpInterface;
using Integround.Components.Log;
using Integround.Components.Log.LogEntries;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Integround.Hubi.WorkerRole.Models;

namespace Integround.Hubi.WorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        private const string AssemblyPath = "assembly";

        private ILogger _log;
        private HttpInterfaceService _httpInterface;
        private Configuration _configuration = new Configuration();

        [ImportMany(typeof(IProcess))]
        private List<IProcess> _processes;

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent _runCompleteEvent = new ManualResetEvent(false);

        public override void Run()
        {
            _log.LogInfo("Integround.Hubi is running.");

            try
            {
                RunAsync(_cancellationTokenSource.Token).Wait();
            }
            finally
            {
                _runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections
            ServicePointManager.DefaultConnectionLimit = 12;

            // Read & deserialize the configuration:
            try
            {
                var confString = GetParameter("Configuration");
                if (string.IsNullOrWhiteSpace(confString))
                    throw new Exception("Configuration is empty.");

                _configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(confString);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Exception during OnStart: Could not read the configuration:" + ex);
            }

            // Initialize the host service:
            try
            {
                CreateLoggers();

                var httpEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints.ContainsKey("HttpInterface.Endpoint")
                   ? RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["HttpInterface.Endpoint"].IPEndpoint
                   : null;
                var httpsEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints.ContainsKey("HttpInterface.EndpointSSL")
                    ? RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["HttpInterface.EndpointSSL"].IPEndpoint
                    : null;

                _httpInterface = new HttpInterfaceService(httpEndpoint, httpsEndpoint, _log);
                _httpInterface.StartAsync().Wait();
            }
            catch (Exception ex)
            {
                _log.LogError("Could not initialize the process host service.", ex);
                return false;
            }

            // Copy assemblies from the blob storage & load them dynamically:
            CopyAssemblies();
            LoadProcesses();

            InitializeProcesses();
            return StartProcesses() && base.OnStart();
        }

        private void CreateLoggers()
        {
            var log = new AggregateLogger();
            log.Add(new TraceLogger());

            var token = _configuration.GetParameterValue("LogEntriesLogger.Token");
            if (!string.IsNullOrWhiteSpace(token))
                log.Add(new LogentriesLogger(token));

            _log = log;
        }

        private void CopyAssemblies()
        {
            try
            {
                // First delete all local assemblies:
                if (Directory.Exists(AssemblyPath))
                    Directory.Delete(AssemblyPath, true);

                var connectionString = GetParameter("StorageConnectionString");
                if (string.IsNullOrWhiteSpace(connectionString))
                    return;

                var storageAccount = CloudStorageAccount.Parse(connectionString);
                var blobClient = storageAccount.CreateCloudBlobClient();

                // TODO: copy assemblies from/to process-specific folders.
                var container = blobClient.GetContainerReference("assembly");
                if (!container.Exists())
                    return;

                var blobs = container.ListBlobs();
                var blobItems = blobs as IListBlobItem[] ?? blobs.ToArray();
                if (!blobItems.Any())
                    return;

                // Copy all files to the local assembly directory:
                Directory.CreateDirectory(AssemblyPath);
                foreach (var blob in blobItems.OfType<CloudBlockBlob>())
                {
                    using (var blobStream = blob.OpenRead())
                    using (var fileStream = File.Create(Path.Combine(AssemblyPath, blob.Name)))
                    {
                        blobStream.CopyTo(fileStream);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Could not copy assemblies from the storage account.", ex);
            }
        }

        private void LoadProcesses()
        {
            if ((_configuration.Processes == null) || !_configuration.Processes.Any())
            {
                _log.LogWarning("No processes configured.");
                return;
            }

            // TODO: Implement appdomain isolation for process assemblies
            try
            {
                var catalog = new AggregateCatalog();

                // Add the base directories to the catalog:
                catalog.Catalogs.Add(new DirectoryCatalog(".", "*.dll"));
                catalog.Catalogs.Add(new DirectoryCatalog(AssemblyPath, "*.dll"));

                // Add each process directory to the catalog (if they exist):
                foreach (var process in _configuration.Processes)
                {
                    var path = Path.Combine(AssemblyPath, process.Name);
                    if (!Directory.Exists(path))
                        continue;

                    catalog.Catalogs.Add(new DirectoryCatalog(path, "*.dll"));
                }

                var container = new CompositionContainer(catalog);

                // HTTP interface service should be populated where needed:
                container.ComposeExportedValue("HttpInterfaceService", _httpInterface);
                container.ComposeParts(this);
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaderExceptions = ex.LoaderExceptions != null
                    ? string.Join(", ", ex.LoaderExceptions.Select(x => x.Message))
                    : string.Empty;
                _log.LogError(string.Concat("Could not load process assemblies: ", loaderExceptions), ex);
            }
            catch (Exception ex)
            {
                _log.LogError("Could not load process assemblies.", ex);
            }
        }

        private void InitializeProcesses()
        {
            if (_processes == null)
                return;

            foreach (var process in _processes)
            {
                try
                {
                    // Find the deserialized process configuration:
                    if ((_configuration?.Processes == null) || !_configuration.Processes.Any(x => string.Equals(x.Name, process.Name)))
                    {
                        throw new Exception("Process configuration not found.");
                    }
                    var processConfiguration = _configuration.Processes.First(x => string.Equals(x.Name, process.Name));

                    // Add process' parameters to a dictionary:
                    var parameterDictionary = new Dictionary<string, string>();
                    if (processConfiguration.Parameters != null)
                    {
                        Array.ForEach(processConfiguration.Parameters, x => parameterDictionary.Add(x.Name, x.Value));
                    }

                    // Then add the common parameters:
                    if (_configuration.Parameters != null)
                    {
                        foreach (var parameter in _configuration.Parameters)
                        {
                            // If the parameter is not defined in the process parameters, add it to the list:
                            if (!parameterDictionary.ContainsKey(parameter.Name))
                                parameterDictionary.Add(parameter.Name, parameter.Value);
                        }
                    }

                    // Then create the process configuration
                    var processConf = new Components.Core.ProcessConfiguration
                    {
                        Name = processConfiguration.Name,
                        Status = (ProcessStatus)processConfiguration.Status,
                        Parameters = new ProcessParameters(parameterDictionary)
                    };

                    process.Initialize(processConf, _log);
                }
                catch (Exception ex)
                {
                    _log.LogError($"Could not initialize process '{process.Name}'.", ex);
                }
            }
        }

        private bool StartProcesses()
        {
            if (_processes == null)
                return true;

            try
            {
                var processesToStart = _processes.Where(x => x.Status == ProcessStatus.Started).ToArray();
                Array.ForEach(processesToStart, x => x.Start());

                _log.LogInfo($"Integround.Hubi started {processesToStart.Length} processes.");
            }
            catch (Exception ex)
            {
                _log.LogError("Starting the processes was unsuccessful", ex);
                return false;
            }

            return true;
        }

        private static string GetParameter(string parameterName)
        {
            try
            {
                return RoleEnvironment.GetConfigurationSettingValue(parameterName);
            }
            catch (Exception)
            {
                throw new Exception($"Could not read parameter '{parameterName}'");
            }
        }

        public override void OnStop()
        {
            // TODO: Stop should prevent new process instances to start and wait for the on-going process instances to stop within a timeout limit.

            try
            {
                _processes.ForEach(x => x.Stop());
            }
            catch (Exception ex)
            {
                _log.LogError("Stopping the services was unsuccessful", ex);
            }

            _log.LogInfo("Integround.Hubi is stopping.");

            _cancellationTokenSource.Cancel();
            _runCompleteEvent.WaitOne();

            base.OnStop();

            _log.LogInfo("Integround.Hubi has stopped.");
        }

        private static async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}
