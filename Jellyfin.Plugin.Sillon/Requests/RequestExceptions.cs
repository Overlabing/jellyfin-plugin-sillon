namespace Jellyfin.Plugin.Sillon.Requests;

/// <summary>A request that does not exist.</summary>
public sealed class RequestNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestNotFoundException"/> class.</summary>
    public RequestNotFoundException()
        : base("demande introuvable")
    {
    }
}

/// <summary>A user touching someone else's request.</summary>
public sealed class RequestForbiddenException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestForbiddenException"/> class.</summary>
    public RequestForbiddenException()
        : base("cette demande n'est pas la vôtre")
    {
    }
}

/// <summary>A change to a request that is no longer live.</summary>
public sealed class RequestNotActiveException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RequestNotActiveException"/> class.</summary>
    public RequestNotActiveException()
        : base("la demande n'est plus active")
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
