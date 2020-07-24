using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Framework;

namespace Microsoft.DotNet.Helix.Sdk
{
    /// <summary>
    /// MSBuild custom task to create HelixWorkItems given xUnit project publish information
    /// </summary>
    public class CreateDotNetTestWorkItems : BaseTask
    {
        /// <summary>
        /// An array of XUnit project workitems containing the following metadata:
        /// - [Required] PublishDirectory: the publish output directory of the XUnit project
        /// - [Required] TargetPath: the output dll path
        /// - [Required] RuntimeTargetFramework: the target framework to run tests on
        /// - [Optional] Arguments: a string of arguments to be passed to the XUnit console runner
        /// The two required parameters will be automatically created if XUnitProject.Identity is set to the path of the XUnit csproj file
        /// </summary>
        [Required]
        public ITaskItem[] Projects { get; set; }

        /// <summary>
        /// Boolean true if this is a posix shell, false if not.
        /// This does not need to be set by a user; it is automatically determined in Microsoft.DotNet.Helix.Sdk.MonoQueue.targets
        /// </summary>
        [Required]
        public bool IsPosixShell { get; set; }

        /// <summary>
        /// Optional timeout for all created workitems
        /// Defaults to 300s
        /// </summary>
        public string WorkItemTimeout { get; set; }

        public string Arguments { get; set; }

        /// <summary>
        /// An array of ITaskItems of type HelixWorkItem
        /// </summary>
        [Output]
        public ITaskItem[] WorkItems { get; set; }

        /// <summary>
        /// The main method of this MSBuild task which calls the asynchronous execution method and
        /// collates logged errors in order to determine the success of HelixWorkItem creation per
        /// provided xUnit project data.
        /// </summary>
        /// <returns>A boolean value indicating the success of HelixWorkItem creation per provided xUnit project data.</returns>
        public override bool Execute()
        {
            ExecuteAsync().GetAwaiter().GetResult();
            return !Log.HasLoggedErrors;
        }

        /// <summary>
        /// The asynchronous execution method for this MSBuild task which verifies the integrity of required properties
        /// and validates their formatting, specifically determining whether the provided xUnit project data have a 
        /// one-to-one mapping. It then creates this mapping before asynchronously preparing the HelixWorkItem TaskItem
        /// objects via the PrepareWorkItem method.
        /// </summary>
        private async Task ExecuteAsync()
        {
            WorkItems = (await Task.WhenAll(Projects.Select(PrepareWorkItemAsync)).ConfigureAwait(false)).Where(wi => wi != null).ToArray();
        }

        private async Task<ITaskItem> PrepareWorkItemAsync(ITaskItem project)
        {
            // Forces this task to run asynchronously
            await Task.Yield();
            
            if (!project.GetRequiredMetadata(Log, "PublishDirectory", out string publishDirectory))
            {
                return null;
            }
            if (!project.GetRequiredMetadata(Log, "TargetPath", out string targetPath))
            {
                return null;
            }
            if (!project.GetRequiredMetadata(Log, "RuntimeTargetFramework", out string runtimeTargetFramework))
            {
                return null;
            }

            project.TryGetMetadata("Arguments", out string arguments);

            string assemblyName = Path.GetFileName(targetPath);

            string loggerArg = IsPosixShell ?
                "--logger \"trx;LogFileName=${HELIX_WORKITEM_ROOT}/testResults.trx\"" :
                "--logger \"trx;LogFileName=%HELIX_WORKITEM_ROOT%\\testResults.trx\"";

            string command = $"dotnet test {assemblyName}{(Arguments != null ? " " + Arguments : "")} {loggerArg} {arguments}";

            Log.LogMessage($"Creating work item with properties Identity: {assemblyName}, PayloadDirectory: {publishDirectory}, Command: {command}");

            TimeSpan timeout = TimeSpan.FromMinutes(5);
            if (!string.IsNullOrEmpty(WorkItemTimeout))
            {
                if (!TimeSpan.TryParse(WorkItemTimeout, out timeout))
                {
                    Log.LogWarning($"Invalid value \"{WorkItemTimeout}\" provided for XUnitWorkItemTimeout; falling back to default value of \"00:05:00\" (5 minutes)");
                }
            }

            if (!project.TryGetMetadata("WorkItemName", out string workItemName))
            {
                if (!project.TryGetMetadata("WorkItemNamePrefix", out string workItemNamePrefix))
                {
                    workItemName = workItemNamePrefix + assemblyName;
                }
                else
                {
                    workItemName = assemblyName;
                }
            }

            return new Microsoft.Build.Utilities.TaskItem(assemblyName, new Dictionary<string, string>()
            {
                { "Identity", workItemName },
                { "PayloadDirectory", publishDirectory },
                { "Command", command },
                { "Timeout", timeout.ToString() },
            });
        }
    }
}
