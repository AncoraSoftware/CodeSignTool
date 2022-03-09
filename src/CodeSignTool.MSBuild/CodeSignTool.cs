using System;
using System.Linq;
using Microsoft.Build.Framework;
using System.IO;
using System.Diagnostics;

namespace Ancora.MSBuild
{
	/// <summary>
	/// Task which wraps the SSL.com's <a href="https://www.ssl.com/guide/esigner-codesigntool-command-guide/">CodeSignTool</a>.
	/// </summary>
	public class CodeSignTool : ContextAwareTask
	{
		readonly static string Censored = "********";

#if NETCOREAPP
		readonly static string JavaExecutable = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "java.exe" : "java";
#else
		readonly static string JavaExecutable = "java.exe";
#endif

		/// <summary>
		/// Gets or sets the files to be signed.
		/// </summary>
		[Required]
		public ITaskItem[] SignFiles { get; set; }

		/// <summary>
		/// Gets or sets the eSigner credential ID to use.
		/// If omitted and the user has only one eSigner code signing certificate, then that certificate will be used. 
		/// If the user has more than one code signing certificate, this parameter is mandatory.
		/// </summary>
		public string CredentialId { get; set; }

		/// <summary>
		/// Gets or sets the SSL.com account username to use.
		/// </summary>
		[Required]
		public string Username { get; set; }

		/// <summary>
		/// Gets or sets the SSL.com account password to use.
		/// </summary>
		[Required]
		public string Password { get; set; }

		/// <summary>
		/// Gets or sets the <a href="https://www.ssl.com/how-to/automate-esigner-ev-code-signing/#ftoc-heading-1">OAuth TOTP secret</a>.
		/// </summary>
		public string TOTPSecret { get; set; }

		/// <summary>
		/// Gets or sets the value to be displayed in the confirmation dialog as the program name
		/// when signing and MSI installer.
		/// </summary>
		public string ProgramName { get; set; }

		/// <summary>
		/// Gets or sets an explicit override for the JAVA_HOME path.
		/// </summary>
		public string JavaHomePath { get; set; }

		/// <summary>
		/// Gets or sets the timeout period for the signing process in milliseconds.
		/// </summary>
		public int TimeoutMilliseconds { get; set; } = 10000;

		/// <summary>
		/// Gets or sets a value indicating if an error should occur if the set of files to be signed is empty.
		/// </summary>
		public bool ErrorOnNoFiles { get; set; } = false;

		protected override bool ExecuteInner()
		{
			try 
			{
				if (SignFiles?.Any() != true)
				{
					if (ErrorOnNoFiles)
					{
						LogCensoredError("No files specified to sign.");
						return false;
					}
					else
					{
						LogCensoredMessage("No files specified to sign.");
						return true;
					}
				}

				var javaPath = FindJava();

				var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssffff");
				var tempDirectory = Path.Combine(Path.GetTempPath(), $"signing-{timestamp}");

				Directory.CreateDirectory(tempDirectory);
				try
				{
					foreach (var file in SignFiles)
					{
						var sourceFile = Path.GetFullPath(file.ItemSpec);
						if (SignFile(javaPath, tempDirectory, sourceFile))
						{
							string filename = Path.GetFileName(file.ItemSpec);
							var tempFile = Path.Combine(tempDirectory, filename);

							// Move file from temporary output directory, overwriting original input.
							File.Delete(sourceFile);
							File.Move(tempFile, sourceFile);
							LogCensoredMessage("Moved file '{0}' to {1}.", tempFile, sourceFile);
						}
					}
				}
				finally
				{
					Directory.Delete(tempDirectory, true);
				}

				return true;
			}
			catch (Exception ex) 
			{
				LogCensoredError($"Exception: {ex}");
				return false;
			}
		}

		private bool SignFile(string javaPath, string outputDirectory, string filePath) 
		{
			if (!File.Exists(filePath))
			{
				throw new FileNotFoundException("Cannot find file to sign.", filePath);
			}

			var codeSignToolDirectory = Path.Combine(ContentDirectory, "CodeSignTool");
			var classPath = Path.Combine(codeSignToolDirectory, "jar", "*");

			var javaParams = $"-cp \"{classPath}\" com.ssl.code.signing.tool.CodeSignTool";
			javaParams += " sign";
			javaParams += $" -username=\"{Username}\"";
			javaParams += $" -password=\"{Password}\"";
			javaParams += $" -input_file_path=\"{filePath}\"";
			javaParams += $" -output_dir_path=\"{outputDirectory}\"";

			if (!string.IsNullOrEmpty(CredentialId))
			{
				javaParams += $" -credential_id={CredentialId}";
			}
			if (!string.IsNullOrEmpty(TOTPSecret))
			{
				javaParams += $" -totp_secret={TOTPSecret}";
			}
			if (!string.IsNullOrEmpty(ProgramName))
			{
				javaParams += $" -program_name={ProgramName}";
			}
			
			LogCensoredMessage("Executing: {0} {1}", javaPath, javaParams);

			using (var proc = new Process())
			{
				proc.StartInfo = new ProcessStartInfo(javaPath, javaParams)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					WindowStyle = ProcessWindowStyle.Hidden,
					WorkingDirectory = codeSignToolDirectory,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};

				// some errors are written to stdout instead of stderr.
				bool errorOutput = false;
				proc.ErrorDataReceived += (sender, e) =>
				{
					if (e.Data != null)
					{
						LogCensoredError(e.Data);
						errorOutput = true;
					}
				};

				proc.OutputDataReceived += (sender, e) =>
				{
					if (e.Data != null)
					{
						// at least one error has been observed written to stdout with an "Error:" prefix.
						if (e.Data.Contains("Error:"))
						{
							LogCensoredError(e.Data);
							errorOutput = true;
						} 
						else 
						{
							LogCensoredMessage(e.Data);
						}
					}
				};

				proc.Start();

				proc.BeginOutputReadLine();
				proc.BeginErrorReadLine();

				// Kill the process if the timeout period is exceeded.
				if(!proc.WaitForExit(TimeoutMilliseconds))
				{
					LogCensoredError("CodeSignTool did not exit in before {0} millisecond timeout.", TimeoutMilliseconds);
					proc.Kill();
					return false;
				}
				
				// Fail if non-zero exit code or error output was written.
				var error = proc.ExitCode != 0 || errorOutput;
				if (error)
				{
					LogCensoredError("CodeSignTool exited code {0}", proc.ExitCode);
				}
				else
				{
					LogCensoredMessage("CodeSignTool exited code {0}", proc.ExitCode);
				}

				return !error;
			}
		}

		private string FindJava()
		{
			var javaHome = JavaHomePath;
			if (string.IsNullOrEmpty(javaHome))
			{
				javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
			}

			var javaSearchPaths = string.IsNullOrEmpty(javaHome) 
				? Enumerable.Empty<string>() : new[] { Path.Combine(javaHome, "bin") };

			string pathEnvVar = Environment.GetEnvironmentVariable("PATH");
			javaSearchPaths = javaSearchPaths.Concat(pathEnvVar.Split(Path.PathSeparator));

			var javaPath = javaSearchPaths
				.Select(path => Path.Combine(path, JavaExecutable))
				.FirstOrDefault(path => File.Exists(path));

			if (string.IsNullOrEmpty(javaPath))
			{
				throw new FileNotFoundException($"Unable to find java executable.", JavaExecutable);
			}

			return javaPath;
		}

		private void LogCensoredMessage(string format, params object[] values)
		{
			Log.LogMessage(string.Format(
				CensorString(format),
				values?.Select(x => CensorString(x.ToString())).ToArray()));
		}

		private void LogCensoredError(string format, params object[] values)
		{
			Log.LogError(string.Format(
				CensorString(format),
				values?.Select(x => CensorString(x.ToString())).ToArray()));
		}

		string CensorString(string value)
		{
			value = value.Replace(Username, Censored).Replace(Password, Censored);
			if (!string.IsNullOrEmpty(CredentialId))
			{
				value = value.Replace(CredentialId, Censored);
			}
			if (!string.IsNullOrEmpty(TOTPSecret))
			{
				value = value.Replace(TOTPSecret, Censored);
			}

			return value;
		}	
	}
}