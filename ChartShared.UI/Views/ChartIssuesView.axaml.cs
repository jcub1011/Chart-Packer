using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChartShared.UI.Controls;
using ChartShared.UI.Resources;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.Views
{
    /// <summary>A chart's findings, grouped by where they are, in one virtualizing list.</summary>
    public partial class ChartIssuesView : UserControl
    {
        public ChartIssuesView()
        {
            InitializeComponent();
            AddHandler(ContextRequestedEvent, OnFindingContextRequested, RoutingStrategies.Tunnel);
        }

        // The menu is a popup of its own, so it's filled as a row asks for it, on the way down, before it opens. Not as it
        // opens: until it's first open it isn't in the row's tree, so it hasn't the finding as its DataContext.
        private void OnFindingContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.ContextMenu is not null) is not { ContextMenu: { } menu } border)
                return;
            menu.Items.Clear();
            if (border.DataContext is FindingItem { OffersFixes: true } item)
                Fill(menu, item);
        }

        // A menu with nothing on it, as where the list can't fix anything, isn't shown at all.
        private void OnFindingMenuOpening(object? sender, CancelEventArgs e)
        {
            if (sender is ContextMenu { ItemCount: 0 })
                e.Cancel = true;
        }

        /// <summary>
        /// Fills a finding's menu with its fixes as they are now: Fix, Fix All For its part and Fix All for a rule with one
        /// way to fix it; a list of the ways, each with those three under it, for one with more; or a greyed-out No Fixes
        /// Available. A way to fix and Fix have the wrench beside them, and each Fix All its own icon; No Fixes Available has
        /// none, as there's nothing to fix.
        /// </summary>
        private static void Fill(ContextMenu menu, FindingItem item)
        {
            var fixes = item.Fixes();
            if (fixes.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = SharedStrings.FindingNoFixesMenuItem, IsEnabled = false });
            }
            else if (fixes is [{ Label: null } only])
            {
                AddActions(menu.Items, item, only);
            }
            else
            {
                foreach (var fix in fixes)
                {
                    var option = new MenuItem { Header = fix.Label, Icon = LineIcon.For("QuickFixStroke") };
                    AddActions(option.Items, item, fix);
                    menu.Items.Add(option);
                }
            }
        }

        private static void AddActions(ItemCollection items, FindingItem item, FindingFix fix)
        {
            items.Add(MenuActions.Create(SharedStrings.FindingFixMenuItem, fix.Fix, "QuickFixStroke"));
            if (fix.FixAllInPart is { } inPart)
                items.Add(MenuActions.Create(item.FixAllInPartText, inPart, "FixAllInPartStroke"));
            if (fix.FixAll is { } all)
                items.Add(MenuActions.Create(SharedStrings.FindingFixAllMenuItem, all, "FixAllStroke"));
        }
    }
}
