namespace SCADA.Runtime.Users;

/// <summary>Ошибка операции над users.json: дубликат логина, короткий пароль,
/// неизвестная роль, битый файл. Сообщения — для администратора станции.</summary>
public class UserStoreException : Exception
{
    public UserStoreException(string message) : base(message) { }
    public UserStoreException(string message, Exception inner) : base(message, inner) { }
}
