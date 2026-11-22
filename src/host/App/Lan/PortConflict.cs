using System.Net.Sockets;

namespace ButtonBox.App.Lan;

public static class PortConflict
{
    /// <summary>
    /// True when a Kestrel start failed because another process already holds the port. Kestrel wraps the socket error
    /// (IOException, then its own exception, then SocketException), so the whole chain is searched.
    /// Windows reports AccessDenied when the other listener bound exclusively.
    /// </summary>
    public static bool IsAddressInUse(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied })
            {
                return true;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse))
            {
                return true;
            }
        }

        return false;
    }
}
