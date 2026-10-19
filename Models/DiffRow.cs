namespace GitManager.Models;

public sealed record DiffRow(string BeforeNumber, string Before, string AfterNumber, string After, bool Removed = false, bool Added = false);
