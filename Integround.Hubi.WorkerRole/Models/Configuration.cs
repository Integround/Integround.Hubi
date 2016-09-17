using System.Linq;

namespace Integround.Hubi.WorkerRole.Models
{
    public class Configuration
    {
        public Parameter[] Parameters { get; set; }
        public ProcessConfiguration[] Processes { get; set; }

        public string GetParameterValue(string name)
        {
            var parameter = Parameters?.FirstOrDefault(x => string.Equals(x.Name, name));
            return parameter?.Value;
        }
    }
    public class Parameter
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
    public class ProcessConfiguration
    {
        public string Name { get; set; }
        public int Status { get; set; }
        public Parameter[] Parameters { get; set; }
    }
}
