﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using EnvDTE80;
using WebCompiler;

namespace WebCompilerVsix
{
    static class CompilerService
    {
        private static ConfigFileProcessor _processor;
        private static DTE2 _dte;

        static CompilerService()
        {
            _dte = WebCompilerPackage._dte;
        }

        private static ConfigFileProcessor Processor
        {
            get
            {
                if (_processor == null)
                {
                    _processor = new ConfigFileProcessor();
                    _processor.ConfigProcessed += ConfigProcessed;
                    _processor.BeforeProcess += (s, e) => { ProjectHelpers.CheckFileOutOfSourceControl(e.Config.GetAbsoluteOutputFile()); };
                    _processor.AfterProcess += AfterProcess;
                    _processor.BeforeWritingSourceMap += (s, e) => { ProjectHelpers.CheckFileOutOfSourceControl(e.ResultFile); };
                    _processor.AfterWritingSourceMap += (s, e) => { ProjectHelpers.AddNestedFile(e.OriginalFile, e.ResultFile); };

                    FileMinifier.BeforeWritingMinFile += (s, e) => { ProjectHelpers.CheckFileOutOfSourceControl(e.ResultFile); };
                    FileMinifier.AfterWritingMinFile += (s, e) => { ProjectHelpers.AddNestedFile(e.OriginalFile, e.ResultFile); };

                    FileMinifier.BeforeWritingGzipFile += (s, e) => { ProjectHelpers.CheckFileOutOfSourceControl(e.ResultFile); };
                    FileMinifier.AfterWritingGzipFile += (s, e) => { ProjectHelpers.AddNestedFile(e.OriginalFile, e.ResultFile); };

                    WebCompiler.CompilerService.Initializing += (s, e) => { _dte.StatusBar.Text = "Installing updated versions of the compilers..."; };
                    WebCompiler.CompilerService.Initialized += (s, e) => { _dte.StatusBar.Text = "Done installing the compiler"; };
                }

                return _processor;
            }
        }

        private static void ConfigProcessed(object sender, ConfigProcessedEventArgs e)
        {
            _dte.StatusBar.Progress(true, $"Compiling \"{e.Config.InputFile}\"", e.AmountProcessed, e.Total);
        }

        public static void Process(string configFile, IEnumerable<Config> configs = null)
        {
            ThreadPool.QueueUserWorkItem((o) =>
            {
                try
                {
                    var result = Processor.Process(configFile, configs);
                    ErrorListService.ProcessCompilerResults(result, configFile);

                    if (!result.Any(c => c.HasErrors))
                    {
                        StatusText("Done compiling");
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    string message = $"{Constants.VSIX_NAME} found an error in {Constants.CONFIG_FILENAME}";
                    Logger.Log(message);
                    StatusText(message);
                    _dte.StatusBar.Progress(false);

                }
                catch (Exception ex)
                {
                    Logger.Log(ex);
                    ShowError(configFile);
                    _dte.StatusBar.Progress(false);
                    StatusText($"{Constants.VSIX_NAME} couldn't compile succesfully");
                }
                finally
                {
                    _dte.StatusBar.Progress(false);
                }
            });
        }

        public static void SourceFileChanged(string configFile, string sourceFile)
        {
            ThreadPool.QueueUserWorkItem((o) =>
            {
                try
                {
                    StatusText($"Compiling \"{Path.GetFileName(sourceFile)}\"...");

                    var result = Processor.SourceFileChanged(configFile, sourceFile);
                    ErrorListService.ProcessCompilerResults(result, configFile);
                }
                catch (FileNotFoundException ex)
                {
                    Logger.Log($"{Constants.VSIX_NAME} could not find \"{ex.FileName}\"");
                    StatusText($"Compiling \"{Path.GetFileName(sourceFile)}\"...");
                }
                catch (Exception ex)
                {
                    Logger.Log(ex);
                    ShowError(configFile);
                }
            });
        }

        private static void StatusText(string message)
        {
            WebCompilerPackage._dispatcher.BeginInvoke(new Action(() =>
            {
                _dte.StatusBar.Text = message;
            }), DispatcherPriority.ApplicationIdle, null);
        }

        private static void ShowError(string configFile)
        {
            MessageBox.Show($"There is an error in the {Constants.CONFIG_FILENAME} file. This could be due to a change in the format after this extension was updated.", Constants.VSIX_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.ServiceNotification);

            if (File.Exists(configFile))
                WebCompilerPackage._dte.ItemOperations.OpenFile(configFile);
        }

        private static void AfterProcess(object sender, CompileFileEventArgs e)
        {
            if (!e.Config.IncludeInProject)
                return;

            var item = _dte.Solution.FindProjectItem(e.Config.FileName);

            if (item == null || item.ContainingProject == null)
                return;

            string input = e.Config.GetAbsoluteInputFile();
            string output = e.Config.GetAbsoluteOutputFile();

            string inputWithOutputExtension = Path.ChangeExtension(input, Path.GetExtension(output));

            if (Path.GetFileName(output).EndsWith(".es5.js"))
                inputWithOutputExtension = Path.ChangeExtension(inputWithOutputExtension, ".es5.js");

            if (inputWithOutputExtension.Equals(output, StringComparison.OrdinalIgnoreCase))
            {
                var inputItem = _dte.Solution.FindProjectItem(input);
                var outputItem = _dte.Solution.FindProjectItem(output);

                // Only add output file to project if it isn't already
                if (inputItem != null && outputItem == null)
                    ProjectHelpers.AddNestedFile(input, output);
            }
            else
            {
                item.ContainingProject.AddFileToProject(e.Config.GetAbsoluteOutputFile());
            }
        }
    }
}
