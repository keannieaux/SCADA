using System.Globalization;
using SCADA.Core.Tags;

namespace SCADA.Drivers.Simulator;

public enum SignalKind { Sine, Square, Ramp, Const }

public readonly record struct SimulatedSignal(SignalKind Kind, double Parameter)
{
    public static SimulatedSignal Parse(string address, TagDataType dataType)
    {
        if (string.IsNullOrWhiteSpace(address))
            return dataType == TagDataType.Discrete
                ? new SimulatedSignal(SignalKind.Square, 5)
                : new SimulatedSignal(SignalKind.Sine, 10);

        var parts = address.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parameter))
            throw new FormatException($"Неверный адрес симуляции: '{address}'. Ожидается 'sin:10', 'square:5', 'ramp:60' или 'const:42'");

        var kind = parts[0].ToLowerInvariant() switch
        {
            "sin" => SignalKind.Sine,
            "square" => SignalKind.Square,
            "ramp" => SignalKind.Ramp,
            "const" => SignalKind.Const,
            _ => throw new FormatException($"Неизвестный тип сигнала в адресе: '{address}'")
        };
        return new SimulatedSignal(kind, parameter);
    }
    public double GetValue(double timeSeconds, TagDefinition tag)
    {
        double phase = tag.Id.Value * 0.618;
        double t = timeSeconds + phase;

        double raw = Kind switch
        {
            SignalKind.Sine   => Math.Sin(2.0 * Math.PI * t / Parameter),   // -1..1
            SignalKind.Square => (t % Parameter) < Parameter / 2 ? 1.0 : 0.0, // 0..1
            SignalKind.Ramp   => (t % Parameter) / Parameter,                // 0..1
            SignalKind.Const  => Parameter,
            _ => 0.0
        };

        if (tag.DataType == TagDataType.Discrete)
            return raw >= 0.5 ? 1.0 : 0.0;

        double min = tag.MinValue ?? -1.0;
        double max = tag.MaxValue ?? 1.0;
        if (Kind == SignalKind.Const) return raw;
        return min + (raw + 1.0) / 2.0 * (max - min);
    }
}
