/*
MIT License

Copyright (c) 2018 Grega Mohorko

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Project: GM.Windows.Tools
Created: 2018-3-7
Author: GregaMohorko
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GM.Utility;
using GM.Windows.Utility;

namespace GM.Windows.Tools
{
	/// <summary>
	/// A tool for working with the Node.Js server.
	/// </summary>
	public class NodeJsServer : IDisposable
	{
		/// <summary>
		/// A message that indicates the start of compilation.
		/// </summary>
		public const string MESSAGE_COMPILE_STARTED = "webpack: Compiling...";
		/// <summary>
		/// A message that indicates a successful end of compilation.
		/// </summary>
		public const string MESSAGE_COMPILE_ENDED = "webpack: Compiled successfully.";
		/// <summary>
		/// A message that indicates an end of compilation, but with warnings.
		/// </summary>
		public const string MESSAGE_COMPILE_ENDED_WITH_WARNINGS = "webpack: Compiled with warnings.";
		/// <summary>
		/// A message that indicates that the compilation has failed.
		/// </summary>
		public const string MESSAGE_COMPILE_FAILED = "webpack: Failed to compile.";
		/// <summary>
		/// A message that indicates that there was an error with npm.
		/// </summary>
		public const string MESSAGE_NPM_ERROR = "npm ERR!";
		/// <summary>
		/// A message that indicates that there was a warning in npm.
		/// </summary>
		public const string MESSAGE_NPM_WARNING = "npm WARN";
		/// <summary>
		/// A message that will be thrown with <see cref="InvalidOperationException"/> when any actions will be made while installing.
		/// </summary>
		public const string MESSAGE_CURRENTLY_INSTALLING = "Node.Js is currently installing packages.";

		private const string SCRIPTS_PREPEND_NODE_PATH = "--scripts-prepend-node-path";

		/// <summary>
		/// A regex expression that determines whether the input text is a text saying that the installation has completed.
		/// </summary>
		public static readonly Regex InstallEndedIdentifier = new Regex(@"^added \d+ packages in \d+.?(\d+)?s$", RegexOptions.Compiled | RegexOptions.Singleline);

		/// <summary>
		/// Occurs when a new compile process starts.
		/// </summary>
		public event EventHandler CompileStarted;
		/// <summary>
		/// Occurs when a compile process has finished. The argument determines whether the compilation was successful.
		/// </summary>
		public event EventHandler<bool> CompileEnded;
		/// <summary>
		/// Occurs when the server has finished starting up (when the initial compilation finishes).
		/// </summary>
		public event EventHandler Started;
		/// <summary>
		/// Occurs when the installation process has finished. The argument determines whether the compilation was successful.
		/// </summary>
		public event EventHandler<bool> InstallEnded;

		/// <summary>
		/// Occurs each time the server writes a line to its standard output.
		/// </summary>
		public event EventHandler<string> OutputLine;

		/// <summary>
		/// The directory in which the Node.Js executables are located.
		/// </summary>
		public readonly string ExecutablesDirectory;

		/// <summary>
		/// The directory in which the Node.Js server is currently running.
		/// </summary>
		public string CurrentDirectory { get; private set; }

		/// <summary>
		/// Determines whether the npm console and the Node.Js server is currently running.
		/// </summary>
		public bool IsRunning { get; private set; }
		/// <summary>
		/// Determines whether the Node.Js server is currently compiling.
		/// </summary>
		public bool IsCompiling { get; private set; }
		/// <summary>
		/// Determines whether the Node.Js server is currently starting.
		/// </summary>
		public bool IsStarting { get; private set; }
		/// <summary>
		/// Determines whether the package installation (npm install command) is currently executing.
		/// </summary>
		public bool IsInstalling { get; private set; }

		/// <summary>
		/// The 'npm' command with the absolute path to the executables directory.
		/// </summary>
		private readonly string NPM;
		/// <summary>
		/// The main process of this tool. Basically a cmd.exe process.
		/// </summary>
		private Process npmProcess;
		/// <summary>
		/// Node processes start by the npm.
		/// </summary>
		private List<Process> nodeProcesses;

		/// <summary>
		/// Initializes a new instance of <see cref="NodeJsServer"/>.
		/// </summary>
		/// <param name="executablesDirectory">The directory in which the Node.JS executables are located.</param>
		public NodeJsServer(string executablesDirectory)
		{
			ExecutablesDirectory = executablesDirectory;
			NPM = $"\"{Path.Combine(ExecutablesDirectory, "npm")}\"";
		}

		/// <summary>
		/// Ensures that the Node.JS server is running in the specified directory.
		/// <para>
		/// Checks if the Node.JS server is running. If it is not, it starts it. If it is and the current directory is not the same as the specified, the server restarts in that directory. If it is the same, but the Node.JS processes were manually killed, they are restarted.
		/// </para>
		/// </summary>
		/// <param name="directory">The directory in which the Node.JS server should be running.</param>
		public void Ensure(string directory)
		{
			if(IsInstalling) {
				throw new InvalidOperationException(MESSAGE_CURRENTLY_INSTALLING);
			}

			if(!IsNpmRunning) {
				Start(directory);
			} else if(CurrentDirectory != directory.ToLower()) {
				// restart in that directory
				KillNodeJs();
				StartNodeJs(directory);
			} else if(!IsStarting && nodeProcesses.All(p => p.HasExited)) {
				// they were probably exited manually
				// lets restart them
				IsRunning = false;
				StartNodeJs(directory);
			}
		}

		/// <summary>
		/// Checks if the <see cref="npmProcess"/> is null or has exited.
		/// </summary>
		private bool IsNpmRunning
		{
			get
			{
				if(npmProcess == null) {
					return false;
				}
				//npmProcess.Refresh();
				return !npmProcess.HasExited;
			}
		}

		/// <summary>
		/// Starts (or restarts) npm console which starts the Node.JS server in the specified directory. If npm is currently already running, it will be killed. If Node.JS is currently already running in the same executables directory, it will be killed.
		/// </summary>
		/// <param name="directory">The directory in which the Node.Js server will be started.</param>
		public void Start(string directory)
		{
			if(IsInstalling) {
				throw new InvalidOperationException(MESSAGE_CURRENTLY_INSTALLING);
			}

			KillNpm();
			StartNpm();
			StartNodeJs(directory);
		}

		/// <summary>
		/// Creates and starts the <see cref="npmProcess"/>.
		/// </summary>
		private void StartNpm()
		{
			npmProcess = new Process
			{
				StartInfo =
				{
					FileName = "cmd.exe",
					UseShellExecute = false,
					CreateNoWindow = true,
					ErrorDialog = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					RedirectStandardInput = true
				}
			};

			npmProcess.OutputDataReceived += NodejsProcess_OutputDataReceived;
			npmProcess.ErrorDataReceived += NpmProcess_ErrorDataReceived;

			npmProcess.Start();
			npmProcess.BeginOutputReadLine();
			npmProcess.BeginErrorReadLine();
		}

		/// <summary>
		/// Starts (or restarts) the Node.JS server in the specified directory. If Node.JS is currently already running in the same executables directory, it will be killed. The npm console must already be running prior to calling this method.
		/// </summary>
		/// <param name="directory">The directory in which the Node.JS server will be started.</param>
		private void StartNodeJs(string directory)
		{
			if(IsRunning) {
				KillNodeJs();
			} else {
				KillAllProcesssesOfThisNodeExecutables();
			}

			if(!IsNpmRunning) {
				throw new InvalidOperationException("Npm was not started yet.");
			}

			CurrentDirectory = directory.ToLower();

			IsRunning = true;
			IsStarting = true;

			// the server starts compiling at the start
			IsCompiling = true;
			CompileStarted?.Invoke(this, EventArgs.Empty);

			npmProcess.StandardInput.WriteLine("cd " + CurrentDirectory);
			npmProcess.StandardInput.WriteLine($"{NPM} start {SCRIPTS_PREPEND_NODE_PATH}");
			//npmProcess.StandardInput.WriteLine($"{NPM} start {SCRIPTS_PREPEND_NODE_PATH} 2> errorlog.txt");
		}

		/// <summary>
		/// Runs the 'npm install' command in the specified directory. The command installs all the needed Node.Js packages into 'node_modules' directory.
		/// <para>If the Node.Js is currently running, it is stopped.</para>
		/// </summary>
		/// <param name="directory">The directory in which the install command will be ran.</param>
		public void Install(string directory)
		{
			if(IsInstalling) {
				throw new InvalidOperationException(MESSAGE_CURRENTLY_INSTALLING);
			}

			if(IsRunning) {
				KillNodeJs();
			} else {
				KillAllProcesssesOfThisNodeExecutables();
			}
			if(!IsNpmRunning) {
				StartNpm();
			}

			IsRunning = true;
			IsInstalling = true;

			npmProcess.StandardInput.WriteLine("cd " + directory);
			npmProcess.StandardInput.WriteLine($"{NPM} install {SCRIPTS_PREPEND_NODE_PATH}");
		}

		/// <summary>
		/// Stops the Node.JS server and the npm console.
		/// </summary>
		public void Stop()
		{
			KillNodeJs();
			KillNpm();
		}

		/// <summary>
		/// If npm is running, it kills it. It also disposes it.
		/// </summary>
		private void KillNpm()
		{
			if(IsNpmRunning) {
				npmProcess.Kill();
			}
			npmProcess?.Dispose();
			npmProcess = null;
		}

		/// <summary>
		/// Kills the Node.JS server, but keeps the npm console open.
		/// </summary>
		private void KillNodeJs()
		{
			// doesn't work, only closes the CMD, but keeps the NodeJs open
			//npmProcess.Kill();

			// doesn't work
			//npmProcess.Close();

			// doesn't work
			//npmProcess.CloseMainWindow();

			// doesn't work
			// send Ctrl + C command
			/*
			npmProcess.StandardInput.Write(KeyCodes.CTRL_C);
			npmProcess.StandardInput.Flush();
			npmProcess.StandardInput.WriteLine('y');
			npmProcess.StandardInput.Flush();
			*/

			// doesn't work when the process is windowless (which in this case, it is)
			// send Ctrl+C command
			//// <summary>
			//// Sends a specified signal to a console process group that shares the console associated with the calling process.
			//// </summary>
			//// <param name="dwCtrlEvent">The type of signal to be generated. This parameter can be one of the following values:<para>CTRL_C_EVENT 0</para><para>CTRL_BREAK_EVENT 1</para></param>
			//// <param name="dwProcessGroupId">The identifier of the process group to receive the signal.</param>
			//[DllImport("kernel32.dll")]
			//private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
			//GenerateConsoleCtrlEvent(1, (uint)npmProcess.Id);
			//npmProcess.StandardInput.WriteLine('y');

			KillAllNodeProcesses();
			IsRunning = false;
			IsStarting = false;
			CurrentDirectory = null;
			if(IsCompiling) {
				IsCompiling = false;
				CompileEnded?.Invoke(this, false);
			}
			if(IsInstalling) {
				IsInstalling = false;
				InstallEnded?.Invoke(this, false);
			}
		}

		/// <summary>
		/// Kills all processes in the <see cref="nodeProcesses"/> list.
		/// </summary>
		private void KillAllNodeProcesses()
		{
			if(nodeProcesses == null) {
				return;
			}
			ProcessUtility.KillAllProcesses(nodeProcesses);
			nodeProcesses = null;
		}

		private void NpmProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
		{
			if(e.Data == null) {
				return;
			}
			OnDataReceived(e.Data.RemoveAllOf("\b"));
		}

		private void NodejsProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			if(e.Data == null) {
				return;
			}
			OnDataReceived(e.Data);
		}

		private void OnDataReceived(string data)
		{
			switch(data) {
				case MESSAGE_COMPILE_STARTED:
					IsCompiling = true;
					CompileStarted?.Invoke(this, EventArgs.Empty);
					break;
				case MESSAGE_COMPILE_ENDED:
				case MESSAGE_COMPILE_ENDED_WITH_WARNINGS:
					if(!IsStarting) {
						IsCompiling = false;
						CompileEnded?.Invoke(this, true);
					} else {
						nodeProcesses = GetNodeProcessesByExecutablePath(ExecutablesDirectory);
						IsStarting = false;
						Started?.Invoke(this, EventArgs.Empty);
					}
					break;
				case MESSAGE_COMPILE_FAILED:
					IsCompiling = false;
					CompileEnded?.Invoke(this, false);
					break;
				default:
					if(IsRunning && data.StartsWith(MESSAGE_NPM_ERROR)) {
						Stop();
						IsStarting = false;
						IsCompiling = false;
						CompileEnded?.Invoke(this, false);
					} else if(IsInstalling) {
						if(data.StartsWith(MESSAGE_NPM_WARNING)) {
							Stop();
							InstallEnded?.Invoke(this, false);
						} else if(InstallEndedIdentifier.IsMatch(data)) {
							IsInstalling = false;
							InstallEnded?.Invoke(this, true);
						}
					}
					break;
			}

			OutputLine?.Invoke(this, data);
		}

		/// <summary>
		/// Kills all processes (if any exist) that use the same executables as this instance of <see cref="NodeJsServer"/>.
		/// </summary>
		private void KillAllProcesssesOfThisNodeExecutables()
		{
			// look for any running node processes in that directory
			List<Process> nodeProcessesAlreadyRunning = GetNodeProcessesByExecutablePath(ExecutablesDirectory);
			// ... and kill them if they exist
			if(!nodeProcessesAlreadyRunning.IsNullOrEmpty()) {
				ProcessUtility.KillAllProcesses(nodeProcessesAlreadyRunning);
			}
		}

		/// <summary>
		/// Finds the node processes whose executables are in the specified directory.
		/// <para>Bolj tocna metoda kot pa <see cref="GetNodeProcessesByTime"/>, ampak pocasnejsa.</para>
		/// </summary>
		/// <param name="executablesDirectory">The directory of the NodeJs executables.</param>
		private static List<Process> GetNodeProcessesByExecutablePath(string executablesDirectory)
		{
			var allNodeProcesses = Process.GetProcessesByName("node");

			string nodeExecutablePath = Path.Combine(executablesDirectory, "node.exe").ToLower();

			List<Process> result = allNodeProcesses.Where(p => ProcessUtility.GetProcessPath(p).ToLower() == nodeExecutablePath).ToList();

			return result;
		}

		/// <summary>
		/// Finds the node processes that are most likely linked to the specified npm process.
		/// <para>Alternativa metodi <see cref="GetNodeProcessesByExecutablePath"/>, ce bi bla ona prepocasna.</para>
		/// </summary>
		/// <param name="npmProcess">The npm process that started the NodeJs.</param>
		private static List<Process> GetNodeProcessesByTime(Process npmProcess)
		{
			var allNodeProcesses = Process.GetProcessesByName("node");

			// select only those 'node' processes that were started within 5 seconds after the npm process
			DateTime npmStartTime = npmProcess.StartTime;
			return allNodeProcesses.Where(p => !p.HasExited && (p.StartTime > npmStartTime) && (p.StartTime - npmStartTime).TotalSeconds < 5).ToList();
		}

		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		/// <summary>
		/// Disposes.
		/// </summary>
		/// <param name="disposing">Determines whether or not to dispose managed objects.</param>
		protected virtual void Dispose(bool disposing)
		{
			if(!disposedValue) {
				if(disposing) {
					// dispose managed state (managed objects)

					Stop();
				}

				// free unmanaged resources (unmanaged objects) and override a finalizer below
				// set large fields to null

				disposedValue = true;
			}
		}

		// Override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~NodeJsServer() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		/// <summary>
		/// Disposes.
		/// </summary>
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// Uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion
	}
}
