using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.Config
{
    public static class LoggerDefaults
    {
        public static void ConfigureDefault()
        {
            Logger.Configure(cfg =>
            {
                cfg.MinimumLevelIn(E.LogTypes.Info)
                   .TemplateIn(
                      "[{Timestamp}] {LogType} {Message}")
                   .AddConsole()
                   .AddFile(file =>
                   {
                       file.Directory = "Log";
                       file.FileName = "Log_Day";
                       file.BatchSize = 100;
                   })
                   .AddOnLog();
            });
        }
    }
}
