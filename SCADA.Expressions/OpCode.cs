namespace SCADA.Expressions;

// Формат операндов:
//   LoadConst, LoadTag   — 1 байт: индекс в пуле констант / индекс тега
//   JumpIfFalse, Jump    — 2 байта (little-endian): АБСОЛЮТНАЯ позиция в коде
//
// Обратные переходы запрещены форматом (ТЗ §11.2): компилятор генерирует
// только переходы вперёд (в грамматике нет циклов), ВМ байткоду из
// проверенного пакета доверяет и целевую позицию не проверяет.
public enum OpCode : byte
{
    LoadConst,
    LoadTag,
    Add,
    Sub,
    Mul,
    Div,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Equal,
    NotEqual,
    Not,
    JumpIfFalse,
    Jump,
    Return,
    CallBuiltin   // операнды: 1 байт — id функции, 1 байт — число аргументов
}
