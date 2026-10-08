namespace RHI.Platform.Linux.Deployment;

/// <summary>Fixed message parts shared by several deployment services (one PT-BR presentation pair each).</summary>
public static class DeploymentMessages
{
    /// <summary>
    /// Appended to an update failure whose rollback could not put the previous file back; the kept copy's path follows,
    /// so the user can restore it by hand. The copy is never deleted in that case.
    /// </summary>
    public const string PreviousFileKeptFragment = " The previous file could not be put back; it is kept at: ";
}
