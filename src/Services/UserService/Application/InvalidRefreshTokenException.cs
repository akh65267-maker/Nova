namespace UserService.Application;

public class InvalidRefreshTokenException : Exception
{
    // One message for every reason (unknown, expired, already used, revoked, user gone): the
    // answer must not tell a caller which of them it was.
    public InvalidRefreshTokenException() : base("The refresh token is invalid or has expired.")
    {
    }
}
