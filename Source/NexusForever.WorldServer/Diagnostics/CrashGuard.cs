using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// Last-chance records for exceptions that end the process. An exception that escapes the world tick or a thread is written to
    /// the ledger, the failure report and /cache/crash.log before the runtime ends the process, and the clean-bot quest truth is flushed.
    /// Character saves cannot be run here: they need the world thread, which is the one that failed, and after an OutOfMemoryException there
    /// is no memory for them. The runtime's own fatal "Out of memory." (an allocation failure inside the runtime) ends the process without
    /// running any managed code, so nothing is recorded for it beyond that console line.
    /// </summary>
    public static class CrashGuard
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public static string CrashLogPath => Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "crash.log");

        public static void Initialise()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                // a faulted task nobody waited for: count it, the process goes on
                Exception inner = e.Exception?.InnerExceptions.FirstOrDefault() ?? e.Exception;
                FailureLedger.Record("task-exception-unobserved", $"{inner?.GetType().Name}: {inner?.Message}", Frames(inner));
                e.SetObserved();
            };
        }

        /// <summary>True for an OutOfMemoryException, also when it is wrapped in an AggregateException (Task.WaitAll in a manager update).</summary>
        public static bool IsOutOfMemory(Exception exception)
            => exception is OutOfMemoryException || exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(e => e is OutOfMemoryException);

        /// <summary>Record an exception that is about to end the process. Never throws.</summary>
        public static void Fatal(string where, Exception exception)
        {
            try
            {
                FailureLedger.Record("fatal-exception", $"{where}: {exception?.GetType().Name}: {exception?.Message}", Frames(exception));
                File.AppendAllText(CrashLogPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {where}: {exception}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }

            try { FailureLedger.WriteNow(); } catch (Exception) { }
            try { QuestTruth.Flush(); } catch (Exception) { }
            try { log.Fatal(exception, $"Process ending: {where}"); LogManager.Flush(); } catch (Exception) { }
        }

        public static string Frames(Exception exception)
            => exception == null ? null : string.Join(" | ", (exception.InnerException?.StackTrace ?? exception.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim()));
    }
}
