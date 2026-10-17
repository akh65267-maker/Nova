namespace UserService.Domain;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);

    /// <summary>
    /// A valid hash of nothing anyone knows. Verifying against it costs what verifying a real one
    /// does, so a login for an address that does not exist, or for a locked account, takes as long
    /// as a wrong password and the timing does not give the account away.
    /// </summary>
    string DummyHash { get; }
}
