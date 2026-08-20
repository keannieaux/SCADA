namespace SCADA.Runtime.Users;

/// <summary>
/// Права берутся из активной сессии локального АРМа (docs/users-plan.md §5).
/// Нет сессии — нет и прав: в режиме AuthMode.Full до входа оператор ничего
/// не делает, а в аудит попадает не пустая строка, а явная отметка.
/// </summary>
public sealed class SessionAccessControl(ISessionService sessions) : IAccessControl
{
    /// <summary>Что пишется в аудит, если действие дошло до ядра без сессии.
    /// Такого быть не должно, но молчать о таком событии нельзя.</summary>
    public const string NoSessionLogin = "не авторизован";

    public string CurrentLogin => sessions.Current?.Login ?? NoSessionLogin;

    public bool HasPermission(string permission)
        => sessions.Current?.HasPermission(permission) ?? false;

    public void NoteActivity() => sessions.Touch();
}
