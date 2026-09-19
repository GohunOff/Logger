using System.Collections.Generic;
using MC.Log.I;

namespace MC.Log.Config
{
    public sealed class LoggerBuilder
    {
        internal List<ILogSink> Sinks { get; } =
            new List<ILogSink>();

        internal string Template { get; private set; }

        internal E.LogTypes MinimumLevel { get; private set; }
            = E.LogTypes.Info;


        public LoggerBuilder MinimumLevelIn(E.LogTypes level)
        {
            MinimumLevel = level;
            return this;
        }


        public LoggerBuilder TemplateIn(string template)
        {
            Template = template;
            return this;
        }


        internal LoggerBuilder AddSink(ILogSink sink)
        {
            if (sink != null)
                Sinks.Add(sink);

            return this;
        }
    }
}
