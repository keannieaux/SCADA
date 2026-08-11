namespace SCADA.Core.Tags;

public readonly record struct TagValue(double Value, long TimeStampUtc, Quality Quality)
{

}
