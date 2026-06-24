namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// The footer status dot. <see cref="Idle"/> = ready/unknown, <see cref="Connected"/> = a key is
    /// stored and the last turn succeeded, <see cref="Working"/> = a turn is in flight, <see cref="Error"/>
    /// = the last turn failed (auth/credits/network). Mapped to a colour by ConnectionStateToBrushConverter.
    /// </summary>
    public enum ConnectionState
    {
        Idle,
        Connected,
        Working,
        Error
    }
}
