using Meloberry.Application.Abstractions;
using Meloberry.Domain.Common;

namespace Meloberry.Application.UseCases.Diagnostics;

public sealed record VerifyWebAppCommand(
    string WebDirectory,
    bool StaticOnly = false,
    int TimeoutSeconds = 30);

public sealed class VerifyWebAppHandler(IHeadlessWebAppVerifier verifier)
{
    public async Task<Result<WebAppVerificationReport>> HandleAsync(
        VerifyWebAppCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.WebDirectory))
            return new Error("Verify.InvalidArgs", "Provide a WebApp output directory.", ErrorType.Validation);

        int timeout = Math.Clamp(command.TimeoutSeconds, 5, 180);
        return await verifier.VerifyAsync(command.WebDirectory, command.StaticOnly, timeout, cancellationToken);
    }
}
