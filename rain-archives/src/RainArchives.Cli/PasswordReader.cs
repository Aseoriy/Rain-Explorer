using System.Text;
using RainArchives.Contracts;

namespace RainArchives.Cli;

internal static class PasswordReader
{
    public static async Task<char[]> ReadAsync(bool prompt, CancellationToken cancellationToken)
    {
        if (prompt)
        {
            Console.Error.Write("Password: ");
        }

        var characters = new char[ProtocolConstants.MaximumSecretBytes];
        var length = 0;
        try
        {
            if (!Console.IsInputRedirected)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    while (!Console.KeyAvailable)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    }

                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.Error.WriteLine();
                        break;
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (length > 0)
                        {
                            characters[--length] = '\0';
                        }

                        continue;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        EnsureCapacity(length, characters.Length);
                        characters[length++] = key.KeyChar;
                    }
                }
            }
            else
            {
                var one = new char[1];
                try
                {
                    while (await Console.In.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
                    {
                        if (one[0] is '\r' or '\n')
                        {
                            break;
                        }

                        EnsureCapacity(length, characters.Length);
                        characters[length++] = one[0];
                        one[0] = '\0';
                    }
                }
                finally
                {
                    Array.Clear(one);
                }
            }

            if (Encoding.UTF8.GetByteCount(characters.AsSpan(0, length)) > ProtocolConstants.MaximumSecretBytes)
            {
                throw new InvalidDataException(
                    $"Password exceeds {ProtocolConstants.MaximumSecretBytes:N0} UTF-8 bytes.");
            }

            return characters.AsSpan(0, length).ToArray();
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    private static void EnsureCapacity(int length, int capacity)
    {
        if (length >= capacity)
        {
            throw new InvalidDataException(
                $"Password exceeds {ProtocolConstants.MaximumSecretBytes:N0} characters.");
        }
    }
}
