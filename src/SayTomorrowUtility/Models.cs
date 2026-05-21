using System.Collections.Generic;

namespace SayTomorrowUtility
{
    internal sealed class InfoRow
    {
        public InfoRow(string section, string name, string value)
        {
            Section = section;
            Name = name;
            Value = value;
        }

        public string Section { get; private set; }
        public string Name { get; private set; }
        public string Value { get; private set; }
    }

    internal sealed class SystemInfoSnapshot
    {
        public SystemInfoSnapshot()
        {
            Rows = new List<InfoRow>();
        }

        public bool IsLaptop { get; set; }
        public List<InfoRow> Rows { get; private set; }
    }

    internal sealed class DefenderStatus
    {
        public bool Available { get; set; }
        public bool? AntivirusEnabled { get; set; }
        public bool? RealTimeProtectionEnabled { get; set; }
        public bool? ServiceEnabled { get; set; }
        public string ProductName { get; set; }
        public string Error { get; set; }
    }
}
