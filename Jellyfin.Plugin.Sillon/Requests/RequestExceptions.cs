namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>A request that does not exist.</summary>
public sealed class RequestNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestNotFoundException"/> class.</summary>
    public RequestNotFoundException()
        : base("envie introuvable")
    {
    }
}

/// <summary>A user touching someone else's request.</summary>
public sealed class RequestForbiddenException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestForbiddenException"/> class.</summary>
    public RequestForbiddenException()
        : base("cet album n'est pas dans votre liste d'envies")
    {
    }
}

/// <summary>A change to a request that is no longer live.</summary>
public sealed class RequestNotActiveException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestNotActiveException"/> class.</summary>
    public RequestNotActiveException()
        : base("cet album n'est plus en attente dans la liste d'envies")
    {
    }
}

/// <summary>The album is not in the catalogue allow-list.</summary>
public sealed class AlbumNotAllowedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AlbumNotAllowedException"/> class.</summary>
    public AlbumNotAllowedException()
        : base("Cet album n'est pas proposé sur ce serveur.")
    {
    }
}

/// <summary>Lidarr refused the album; the detail is logged, never sent to clients.</summary>
public sealed class LidarrAddException : Exception
{
    /// <summary>The message stored on the request and sent to clients.</summary>
    public const string Text = "ajout Lidarr refusé";

    /// <summary>Initializes a new instance of the <see cref="LidarrAddException"/> class.</summary>
    public LidarrAddException()
        : base(Text)
    {
    }
}

/// <summary>Lidarr could not be reached.</summary>
public sealed class LidarrUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LidarrUnavailableException"/> class.</summary>
    public LidarrUnavailableException()
        : base("Lidarr indisponible")
    {
    }
}

/// <summary>A request that cannot be retried: wrong state, or no Lidarr to retry with.</summary>
public sealed class RequestNotRetryableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestNotRetryableException"/> class.</summary>
    /// <param name="message">The message shown to the user.</param>
    public RequestNotRetryableException(string message = "cette envie ne peut pas être relancée")
        : base(message)
    {
    }
}
