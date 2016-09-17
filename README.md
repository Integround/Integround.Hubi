# Integround Hubi

Integround Hubi is a simple open source platform for hosting your integration processes in the cloud. It is released under the MIT license.

Integround Hubi is installed to a Azure Cloud Service. 
It loads the integrations processes dynamically from Azure blob storage on service start. It can host any process implementing the ***Integround.Components.Core.IProcess*** interface so you are pretty free to implement whatever you want.
Integround Hubi also hosts an HTTP interface service allowing your processes to register endpoints for their incoming HTTP/HTTPS traffic.

This code is ready to be built and installed to Azure. If you need to enable an HTTPS endpoint, just configure the HTTPS endpoint in the service definition & configuration files and build the deployment package. You also need to install your SSL certificate to your Azure service.

Also check [Integround Components][1], a collection of open source integration components to help you build custom integration solutions easily. These components can be used in any .NET application to make it easier to execute many integration-related tasks without writing all code by yourself.

## Tutorials
Coming soon...
### Configuration parameters
Configuration parameters tell the platform where the your integration processe are found and how they should be configured.

- **StorageConnectionString** parameter should contain a connection string of the Azure blob storage account.

- **Configuration** parameter contains the process configuration as a JSON structure. 
All process-related parameters, like service addresses, passwords etc. are defined in the configuration JSON. 
It also defines the process statuses telling which process to start when the 

Here's an example of the service configuration JSON:
```json
{
	"Parameters": [{
		"Name": "Backend.Address",
		"Value": "http://huuhaa.fi/api/getdata"
	},
	{
		"Name": "Backend.UserName",
		"Value": "Integround"
	},
	{
		"Name": "Backend.Password",
		"Value": "xxx"
	},
	{
		"Name": "LogEntriesLogger.Token",
		"Value": "yyy"
	}],
	"Processes": [{
		"Name": "My.Active.Process",
		"Status": 1,
		"Parameters": [{
			"Name": "My.First.Process.Parameter",
			"Value": "parameterValue"
		}]
	},
	{
		"Name": "My.Inactive.Process",
		"Status": 0
	}]
}
```

## Integround
[Integround Oy][2] was incorporated in Helsinki, Finland in June 2012. We are specialized in system integration solutions and have vast experience in .NET development & cloud integrations hosted in Microsoft Azure. We are committed to an open source strategy to support successful integration projects everywhere.

We welcome all your feedback and contributions. **Fork, live long and prosper.**

[1]: https://github.com/Integround/Integround.Components
[2]: http://www.integround.com