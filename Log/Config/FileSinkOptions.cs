using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.Config
{
    public sealed class FileSinkOptions
    {
        public string Directory { get; set; } = "Log";

        public string FileName { get; set; } = "Log_Day";

        public int RetentionMonths { get; set; } = 3;

        public int BatchSize { get; set; } = 100;

        public E.LogTypes MinimumLevel { get; set; } =
            E.LogTypes.Info;
    }
}
