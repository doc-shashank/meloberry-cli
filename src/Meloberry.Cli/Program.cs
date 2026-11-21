using System.Text;
using Meloberry.Cli.Abstractions;
using Meloberry.Cli.Hosting;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliApplicationRunner.ExecuteAsync(args, new SystemConsole(), cancellationToken: cts.Token);
