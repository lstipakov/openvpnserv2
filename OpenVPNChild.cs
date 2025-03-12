﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace OpenVpn
{
    class OpenVpnChild
    {
        string logFile;
        Process process;
        System.Timers.Timer restartTimer;
        OpenVpnServiceConfiguration config;
        string configFile;
        string exitEvent;
        private CancellationTokenSource exitPollingToken = new CancellationTokenSource();

        public OpenVpnChild(OpenVpnServiceConfiguration config, string configFile)
        {
            this.config = config;
            /// SET UP LOG FILES
            /* Because we will be using the filenames in our closures,
             * so make sure we are working on a copy */
            this.configFile = String.Copy(configFile);
            this.exitEvent = Path.GetFileName(configFile) + "_" + Process.GetCurrentProcess().Id.ToString();
            var justFilename = Path.GetFileName(configFile);
            logFile = Path.Combine(config.logDir, justFilename.Substring(0, justFilename.Length - config.configExt.Length) + ".log");

            // FIXME: if (!init_security_attributes_allow_all (&sa))
            //{
            //    MSG (M_SYSERR, "InitializeSecurityDescriptor start_" PACKAGE " failed");
            //    goto finish;
            //}
        }

        // set exit event so that openvpn will terminate
        public void SignalProcess()
        {
            if (restartTimer != null)
            {
                restartTimer.Stop();
            }
            try
            {
                if (process != null && !process.HasExited)
                {
                    try
                    {
                        config.LogMessage($"Signalling PID {process.Id} for config {configFile} to exit");

                        using (var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, exitEvent))
                        {
                            waitHandle.Set();  // Signal OpenVPN to exit gracefully
                        }

                        exitPollingToken.Cancel(); // Stop monitoring
                    }
                    catch (IOException e)
                    {
                        config.LogMessage("IOException creating exit event named '" + exitEvent + "' " + e.Message + e.StackTrace, EventLogEntryType.Error);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        config.LogMessage("UnauthorizedAccessException creating exit event named '" + exitEvent + "' " + e.Message + e.StackTrace, EventLogEntryType.Error);
                    }
                    catch (WaitHandleCannotBeOpenedException e)
                    {
                        config.LogMessage("WaitHandleCannotBeOpenedException creating exit event named '" + exitEvent + "' " + e.Message + e.StackTrace, EventLogEntryType.Error);
                    }
                    catch (ArgumentException e)
                    {
                        config.LogMessage("ArgumentException creating exit event named '" + exitEvent + "' " + e.Message + e.StackTrace, EventLogEntryType.Error);
                    }
                }
            }
            catch (InvalidOperationException) { }
        }

        /// **Polling loop to monitor process exit**
        private async void MonitorProcessExit()
        {
            if (process == null) return;

            config.LogMessage($"Started polling for OpenVPN process, PID {process.Id}");

            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(1000, exitPollingToken.Token);
                }

                config.LogMessage($"Process {process.Id} has exited.", EventLogEntryType.Warning);
                RestartAfterDelay(10000);
            }
            catch (TaskCanceledException)
            {
                config.LogMessage("Process monitoring cancelled.");
            }
            catch (Exception ex)
            {
                config.LogMessage($"Error in MonitorProcessExit: {ex.Message}", EventLogEntryType.Error);
            }
        }

        /// **Restart after a delay**
        private void RestartAfterDelay(int delayMs)
        {
            config.LogMessage($"Restarting process for {configFile} in {delayMs / 1000} sec.");

            restartTimer = new System.Timers.Timer(delayMs);
            restartTimer.AutoReset = false;
            restartTimer.Elapsed += (object source, ElapsedEventArgs ev) =>
            {
                Start();
            };
            restartTimer.Start();
        }

        private const string PipeName = @"openvpn\service";

        public void Start()
        {
            using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                config.LogMessage("Connecting to iservice pipe...");
                pipeClient.Connect(5000);

                using (var writer = new BinaryWriter(pipeClient, Encoding.Unicode))
                using (var reader = new StreamReader(pipeClient, Encoding.Unicode))
                {
                    // send startup info
                    var logOption = config.logAppend ? "--log-append " : "--log";
                    var cmdLine = $"{logOption} \"{logFile}\" --config \"{configFile}\" --service \"{exitEvent}\" 0 --pull-filter ignore route-method";

                    // config_dir + \0 + options + \0 + password + \0
                    var startupInfo = $"{config.configDir}\0{cmdLine}\0\0";

                    byte[] messageBytes = Encoding.Unicode.GetBytes(startupInfo);
                    writer.Write(messageBytes);
                    writer.Flush();

                    config.LogMessage("Sent startupInfo to iservice");

                    // read openvpn process pid from the pipe
                    string[] lines = { reader.ReadLine(), reader.ReadLine() };

                    config.LogMessage($"Read from iservice: {string.Join(" ", lines)}");
                    var errorCode = Convert.ToInt32(lines[0], 16);

                    if (errorCode == 0)
                    {
                        var pid = Convert.ToInt32(lines[1], 16);
                        process = Process.GetProcessById(pid);

                        exitPollingToken = new CancellationTokenSource();
                        Task.Run(() => MonitorProcessExit(), exitPollingToken.Token);

                        config.LogMessage($"Started monitoring OpenVPN process, PID {pid}");
                    } else
                    {
                        config.LogMessage("Error getting openvpn process PID", EventLogEntryType.Error);
                    }
                }
            }
        }
    }
}
