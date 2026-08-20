namespace SCADA.Core.Users;

/// <summary>
/// Роль — именованный набор прав (docs/users-plan.md §2.2). Данные проекта:
/// исходная форма — roles.json, компилируемая — секция roles.bin пакета.
/// Права — строки: системные из <see cref="SystemPermissions"/> плюс
/// произвольные проектные. Класс с set-свойствами, а не позиционный record:
/// поля растут (описание, встроенность) без смены форматов.
/// </summary>
public class RoleDefinition
{
    /// <summary>Уникальное имя роли ("Оператор").</summary>
    public required string Name { get; set; }

    /// <summary>Права роли: системные константы и/или проектные строки.</summary>
    public List<string> Permissions { get; set; } = [];
}
