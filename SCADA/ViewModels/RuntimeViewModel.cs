using CommunityToolkit.Mvvm.ComponentModel;
using SCADA.Views;

namespace SCADA.ViewModels;

public partial class RuntimeViewModel: ViewModelBase
{
    public TagsViewModel TagsViewModel{ get; }
    public RuntimeViewModel(TagsViewModel tagsViewModel)
    {
        TagsViewModel=tagsViewModel;
    }
}
