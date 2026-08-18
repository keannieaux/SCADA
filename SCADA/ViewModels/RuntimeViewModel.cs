using CommunityToolkit.Mvvm.ComponentModel;

namespace SCADA.ViewModels;

public partial class RuntimeViewModel: ViewModelBase
{
    public TagsViewModel TagsViewModel{ get; }
    public SchemesViewModel SchemesViewModel { get; }

    public RuntimeViewModel(TagsViewModel tagsViewModel, SchemesViewModel schemesViewModel)
    {
        TagsViewModel=tagsViewModel;
        SchemesViewModel = schemesViewModel;
        SchemesViewModel.Canvas.StartLive();
    }
}
