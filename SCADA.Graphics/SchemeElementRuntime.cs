using SCADA.Core.Schemes;

namespace SCADA.Graphics;

public sealed class SchemeElementRuntime
{
    public CompiledSchemeElement Compiled {get;}
    public bool QualityBad {get;set;}
    public bool BlinkActive {get;set;}

    private readonly PropertyValue[] _values;
    private readonly Dictionary<int, int> _slotByPropertyId;

    public SchemeElementRuntime(CompiledSchemeElement compiled)
    {
        Compiled=compiled;

        var defs=ElementSchemas.For(compiled.Source.Kind);
        _values=new PropertyValue[defs.Count];
        _slotByPropertyId=new Dictionary<int, int>(defs.Count);

        for(int i =0; i < defs.Count;i++)
        {
            _slotByPropertyId[defs[i].Id]=i;
            _values[i]=defs[i].Default;
        }

        foreach(var property in compiled.Source.Properties)
            if(_slotByPropertyId.TryGetValue(property.PropertyId, out int slot))
                _values[slot]=property.Value;
    }

    public PropertyValue Get(int propertyId)=>_values[_slotByPropertyId[propertyId]];
    public void Set(int propertyId, PropertyValue value) => _values[_slotByPropertyId[propertyId]]=value;
}
