namespace SCADA.Expressions;

// Формат операндов (все — 4 байта int, little-endian, кроме CallBuiltin):
//   LoadConst, LoadTag   — индекс в пуле констант / индекс тега
//   JumpIfFalse, Jump    — АБСОЛЮТНАЯ позиция в коде
//   CallBuiltin          — 4 байта id функции, 1 байт число аргументов
// Один размер на все индексы — простой декодер в горячем цикле ВМ;
// потолок в 2 млрд снимает ограничение на число тегов навсегда.
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
