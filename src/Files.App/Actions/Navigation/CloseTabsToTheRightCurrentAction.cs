﻿// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	internal sealed partial class CloseTabsToTheRightCurrentAction : CloseTabBaseAction
	{
		public override string Label
			=> Strings.CloseTabsToTheRight.GetLocalizedResource();

		public override string Description
			=> Strings.CloseTabsToTheRightCurrentDescription.GetLocalizedResource();

		public CloseTabsToTheRightCurrentAction()
		{
		}

		public override Task ExecuteAsync(object? parameter = null)
		{
			MultitaskingTabsHelpers.CloseTabsToTheRight(context.CurrentTabItem, context.Control!);

			return Task.CompletedTask;
		}

		protected override bool GetIsExecutable()
		{
			return context.Control is not null && context.CurrentTabIndex < context.TabCount - 1;
		}

		protected override void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IMultitaskingContext.Control):
				case nameof(IMultitaskingContext.TabCount):
				case nameof(IMultitaskingContext.SelectedTabIndex):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
