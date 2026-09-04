Console.WriteLine("Hello, World!");

var forceInteractive = args.Contains("--reauth", StringComparer.OrdinalIgnoreCase);
var signOut = args.Contains("--sign-out", StringComparer.OrdinalIgnoreCase);

try
{
    var secretsDirectory = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "client_secrets.json"))
        ? Directory.GetCurrentDirectory()
        : AppContext.BaseDirectory;

    var google = GoogleDeviceSignOn.FromEnvironmentOrSecretsFile(secretsDirectory);

    if (signOut)
    {
        google.SignOut();
        Console.WriteLine("Signed out. Local Google tokens were removed.");
        return;
    }

    var user = await google.SignInAsync(forceInteractive, CancellationToken.None);
    var displayName = string.IsNullOrWhiteSpace(user.Name) ? "Google user" : user.Name;
    var email = string.IsNullOrWhiteSpace(user.Email) ? user.Subject : user.Email;
    Console.WriteLine($"Signed in as {displayName} <{email}>.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
