using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.Config
{
    public static class LoggerBuilderExtensions
    {

        public static LoggerBuilder AddConsole(
            this LoggerBuilder builder)
        {
            builder.AddSink(
                new Sink.ConsoleSink(
                    builder.MinimumLevel));

            return builder;
        }


        public static LoggerBuilder AddOnLog(
            this LoggerBuilder builder)
        {
            builder.AddSink(
                new Sink.OnLogSink(
                    builder.MinimumLevel));

            return builder;
        }


        public static LoggerBuilder AddFile(
            this LoggerBuilder builder,
            Action<FileSinkOptions> configure)
        {
            var options = new FileSinkOptions();

            configure(options);


            var manager =
                new LogFileManager(options.Directory);


            builder.AddSink(
                new Sink.FileSink(
                    manager,
                    options.FileName,
                    options.MinimumLevel));


            return builder;
        }
    }
}
