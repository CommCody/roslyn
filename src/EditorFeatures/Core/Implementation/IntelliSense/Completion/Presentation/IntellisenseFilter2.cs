﻿using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.VisualStudio.Language.Intellisense;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion.Presentation
{
    internal class IntellisenseFilter2 : IntellisenseFilter
    {
        private readonly CompletionSet3 _completionSet;
        public readonly CompletionItemFilter CompletionItemFilter;

        public IntellisenseFilter2(
            CompletionSet3 completionSet, CompletionItemFilter filter, string language)
            : base(ImageMonikers.GetImageMoniker(filter.Tags, language), GetToolTip(filter),
                   filter.AccessKey.ToString(), automationText: filter.Tags[0])
        {
            _completionSet = completionSet;
            CompletionItemFilter = filter;
        }

        private static string GetToolTip(CompletionItemFilter filter)
        {
            return filter.DisplayText + " (Alt+" + char.ToUpper(filter.AccessKey) + ")";
        }

        public override bool IsChecked
        {
            get
            {
                return base.IsChecked;
            }

            set
            {
                base.IsChecked = value;

                if (_completionSet != null)
                {
                    _completionSet.OnIntelliSenseFiltersChanged();
                }
            }
        }
    }
}
