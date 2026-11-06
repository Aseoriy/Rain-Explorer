using System.Security.Cryptography;
using RainArchives.Cli;

CliProgram.ConfigureConsoleEncoding();
var exitCode = await CliProgram.RunAsync(args).ConfigureAwait(false);
return exitCode;
