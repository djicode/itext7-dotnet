/*

This file is part of the iText (R) project.
Copyright (c) 1998-2017 iText Group NV
Authors: Bruno Lowagie, Paulo Soares, et al.

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License version 3
as published by the Free Software Foundation with the addition of the
following permission added to Section 15 as permitted in Section 7(a):
FOR ANY PART OF THE COVERED WORK IN WHICH THE COPYRIGHT IS OWNED BY
ITEXT GROUP. ITEXT GROUP DISCLAIMS THE WARRANTY OF NON INFRINGEMENT
OF THIRD PARTY RIGHTS

This program is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
or FITNESS FOR A PARTICULAR PURPOSE.
See the GNU Affero General Public License for more details.
You should have received a copy of the GNU Affero General Public License
along with this program; if not, see http://www.gnu.org/licenses or write to
the Free Software Foundation, Inc., 51 Franklin Street, Fifth Floor,
Boston, MA, 02110-1301 USA, or download the license from the following URL:
http://itextpdf.com/terms-of-use/

The interactive user interfaces in modified source and object code versions
of this program must display Appropriate Legal Notices, as required under
Section 5 of the GNU Affero General Public License.

In accordance with Section 7(b) of the GNU Affero General Public License,
a covered work must retain the producer line in every PDF that is created
or manipulated using iText.

You can be released from the requirements of the license by purchasing
a commercial license. Buying such a license is mandatory as soon as you
develop commercial activities involving the iText software without
disclosing the source code of your own applications.
These activities include: offering paid services to customers as an ASP,
serving PDFs on the fly in a web application, shipping iText with a closed
source product.

For more information, please contact iText Software Corp. at this
address: sales@itextpdf.com
*/
using System;
using System.Collections.Generic;
using System.Text;
using iText.IO.Font.Otf;
using iText.IO.Util;
using iText.Kernel.Geom;
using iText.Layout.Element;
using iText.Layout.Layout;
using iText.Layout.Minmaxwidth;
using iText.Layout.Properties;

namespace iText.Layout.Renderer {
    public class LineRenderer : AbstractRenderer {
        protected internal float maxAscent;

        protected internal float maxDescent;

        protected internal byte[] levels;

        // bidi levels
        public override LayoutResult Layout(LayoutContext layoutContext) {
            Rectangle layoutBox = layoutContext.GetArea().GetBBox().Clone();
            IList<Rectangle> floatRendererAreas = layoutContext.GetFloatRendererAreas();
            if (floatRendererAreas != null) {
                FloatingHelper.AdjustLineAreaAccordingToFloats(floatRendererAreas, layoutBox);
            }
            occupiedArea = new LayoutArea(layoutContext.GetArea().GetPageNumber(), layoutBox.Clone().MoveUp(layoutBox.
                GetHeight()).SetHeight(0).SetWidth(0));
            float curWidth = 0;
            maxAscent = 0;
            maxDescent = 0;
            int childPos = 0;
            MinMaxWidth minMaxWidth = new MinMaxWidth(0, layoutBox.GetWidth());
            AbstractWidthHandler widthHandler = new MaxSumWidthHandler(minMaxWidth);
            UpdateChildrenParent();
            ResolveChildrenFonts();
            int totalNumberOfTrimmedGlyphs = TrimFirst();
            BaseDirection? baseDirection = ApplyOtf();
            UpdateBidiLevels(totalNumberOfTrimmedGlyphs, baseDirection);
            bool anythingPlaced = false;
            TabStop hangingTabStop = null;
            LineLayoutResult result = null;
            bool floatsPlaced = false;
            IList<IRenderer> overflowFloats = new List<IRenderer>();
            int lastTabIndex = 0;
            while (childPos < childRenderers.Count) {
                IRenderer childRenderer = childRenderers[childPos];
                LayoutResult childResult;
                Rectangle bbox = new Rectangle(layoutBox.GetX() + curWidth, layoutBox.GetY(), layoutBox.GetWidth() - curWidth
                    , layoutBox.GetHeight());
                if (childRenderer is TextRenderer) {
                    // Delete these properties in case of relayout. We might have applied them during justify().
                    childRenderer.DeleteOwnProperty(Property.CHARACTER_SPACING);
                    childRenderer.DeleteOwnProperty(Property.WORD_SPACING);
                }
                else {
                    if (childRenderer is TabRenderer) {
                        if (hangingTabStop != null) {
                            IRenderer tabRenderer = childRenderers[childPos - 1];
                            tabRenderer.Layout(new LayoutContext(new LayoutArea(layoutContext.GetArea().GetPageNumber(), bbox)));
                            curWidth += tabRenderer.GetOccupiedArea().GetBBox().GetWidth();
                            widthHandler.UpdateMaxChildWidth(tabRenderer.GetOccupiedArea().GetBBox().GetWidth());
                        }
                        hangingTabStop = CalculateTab(childRenderer, curWidth, layoutBox.GetWidth());
                        if (childPos == childRenderers.Count - 1) {
                            hangingTabStop = null;
                        }
                        if (hangingTabStop != null) {
                            lastTabIndex = childPos;
                            ++childPos;
                            continue;
                        }
                    }
                }
                if (hangingTabStop != null && hangingTabStop.GetTabAlignment() == TabAlignment.ANCHOR && childRenderer is 
                    TextRenderer) {
                    childRenderer.SetProperty(Property.TAB_ANCHOR, hangingTabStop.GetTabAnchor());
                }
                // Normalize child width
                Object childWidth = childRenderer.GetProperty<Object>(Property.WIDTH);
                bool childWidthWasReplaced = false;
                bool childRendererHasOwnWidthProperty = childRenderer.HasOwnProperty(Property.WIDTH);
                if (childWidth is UnitValue && ((UnitValue)childWidth).IsPercentValue()) {
                    float normalizedChildWidth = ((UnitValue)childWidth).GetValue() / 100 * layoutContext.GetArea().GetBBox().
                        GetWidth();
                    // Decrease the calculated width by margins, paddings and borders so that even for 100% width the content definitely fits
                    // TODO DEVSIX-1174 fix depending of box-sizing
                    if (childRenderer is AbstractRenderer) {
                        Rectangle dummyRect = new Rectangle(normalizedChildWidth, 0);
                        ((AbstractRenderer)childRenderer).ApplyMargins(dummyRect, false);
                        ((AbstractRenderer)childRenderer).ApplyBorderBox(dummyRect, false);
                        ((AbstractRenderer)childRenderer).ApplyPaddings(dummyRect, false);
                        normalizedChildWidth = dummyRect.GetWidth();
                    }
                    if (normalizedChildWidth > 0) {
                        childRenderer.SetProperty(Property.WIDTH, UnitValue.CreatePointValue(normalizedChildWidth));
                        childWidthWasReplaced = true;
                    }
                }
                FloatPropertyValue? kidFloatPropertyVal = childRenderer.GetProperty<FloatPropertyValue?>(Property.FLOAT);
                bool isChildFloating = childRenderer is AbstractRenderer && FloatingHelper.IsRendererFloating(childRenderer
                    , kidFloatPropertyVal);
                if (isChildFloating) {
                    childResult = null;
                    MinMaxWidth kidMinMaxWidth = FloatingHelper.CalculateMinMaxWidthForFloat((AbstractRenderer)childRenderer, 
                        kidFloatPropertyVal);
                    float floatingBoxFullWidth = kidMinMaxWidth.GetMaxWidth() + kidMinMaxWidth.GetAdditionalWidth();
                    // TODO width will be recalculated on float layout;
                    // also not taking it into account (i.e. not setting it on child renderer) results in differences with html
                    // when floating span is split on other line;
                    // TODO may be process floating spans as inline blocks always?
                    if (overflowFloats.IsEmpty() && (!anythingPlaced || floatingBoxFullWidth <= bbox.GetWidth())) {
                        childResult = childRenderer.Layout(new LayoutContext(new LayoutArea(layoutContext.GetArea().GetPageNumber(
                            ), layoutContext.GetArea().GetBBox().Clone()), null, floatRendererAreas));
                    }
                    // Get back child width so that it's not lost
                    if (childWidthWasReplaced) {
                        if (childRendererHasOwnWidthProperty) {
                            childRenderer.SetProperty(Property.WIDTH, childWidth);
                        }
                        else {
                            childRenderer.DeleteOwnProperty(Property.WIDTH);
                        }
                    }
                    float minChildWidth = 0;
                    float maxChildWidth = 0;
                    if (childResult is MinMaxWidthLayoutResult) {
                        if (!childWidthWasReplaced) {
                            minChildWidth = ((MinMaxWidthLayoutResult)childResult).GetNotNullMinMaxWidth(bbox.GetWidth()).GetMinWidth(
                                );
                        }
                        // TODO if percents width was used, max width might be huge
                        maxChildWidth = ((MinMaxWidthLayoutResult)childResult).GetNotNullMinMaxWidth(bbox.GetWidth()).GetMaxWidth(
                            );
                    }
                    widthHandler.UpdateMinChildWidth(minChildWidth);
                    widthHandler.UpdateMaxChildWidth(maxChildWidth);
                    if (childResult == null || childResult.GetStatus() == LayoutResult.NOTHING) {
                        overflowFloats.Add(childRenderer);
                    }
                    else {
                        if (childResult.GetStatus() == LayoutResult.PARTIAL) {
                            floatsPlaced = true;
                            LineRenderer[] split = SplitNotFittingFloat(childPos, childResult);
                            IRenderer splitRenderer = childResult.GetSplitRenderer();
                            if (splitRenderer is TextRenderer) {
                                ((TextRenderer)splitRenderer).TrimFirst();
                                ((TextRenderer)splitRenderer).TrimLast();
                            }
                            // ensure no other thing (like text wrapping the float) will occupy the line
                            splitRenderer.GetOccupiedArea().GetBBox().SetWidth(layoutContext.GetArea().GetBBox().GetWidth());
                            result = new LineLayoutResult(LayoutResult.PARTIAL, occupiedArea, split[0], split[1], null);
                            break;
                        }
                        else {
                            floatsPlaced = true;
                            if (childRenderer is TextRenderer) {
                                ((TextRenderer)childRenderer).TrimFirst();
                                ((TextRenderer)childRenderer).TrimLast();
                            }
                            AdjustLineOnFloatPlaced(layoutBox, childPos, kidFloatPropertyVal, childRenderer.GetOccupiedArea().GetBBox(
                                ));
                        }
                    }
                    childPos++;
                    if (!anythingPlaced && childResult != null && childResult.GetStatus() == LayoutResult.NOTHING && floatRendererAreas
                        .IsEmpty()) {
                        if (IsFirstOnRootArea()) {
                            // Current line is empty, kid returns nothing and neither floats nor content
                            // were met on root area (e.g. page area) - return NOTHING, don't layout other line content,
                            // expect FORCED_PLACEMENT to be set.
                            break;
                        }
                    }
                    continue;
                }
                childResult = childRenderer.Layout(new LayoutContext(new LayoutArea(layoutContext.GetArea().GetPageNumber(
                    ), bbox)));
                // Get back child width so that it's not lost
                if (childWidthWasReplaced) {
                    if (childRendererHasOwnWidthProperty) {
                        childRenderer.SetProperty(Property.WIDTH, childWidth);
                    }
                    else {
                        childRenderer.DeleteOwnProperty(Property.WIDTH);
                    }
                }
                float minChildWidth_1 = 0;
                float maxChildWidth_1 = 0;
                if (childResult is MinMaxWidthLayoutResult) {
                    if (!childWidthWasReplaced) {
                        minChildWidth_1 = ((MinMaxWidthLayoutResult)childResult).GetNotNullMinMaxWidth(bbox.GetWidth()).GetMinWidth
                            ();
                    }
                    maxChildWidth_1 = ((MinMaxWidthLayoutResult)childResult).GetNotNullMinMaxWidth(bbox.GetWidth()).GetMaxWidth
                        ();
                }
                float childAscent = 0;
                float childDescent = 0;
                if (childRenderer is ILeafElementRenderer) {
                    childAscent = ((ILeafElementRenderer)childRenderer).GetAscent();
                    childDescent = ((ILeafElementRenderer)childRenderer).GetDescent();
                }
                maxAscent = Math.Max(maxAscent, childAscent);
                maxDescent = Math.Min(maxDescent, childDescent);
                float maxHeight = maxAscent - maxDescent;
                bool newLineOccurred = (childResult is TextLayoutResult && ((TextLayoutResult)childResult).IsSplitForcedByNewline
                    ());
                bool shouldBreakLayouting = childResult.GetStatus() != LayoutResult.FULL || newLineOccurred;
                if (hangingTabStop != null && (TabAlignment.LEFT == hangingTabStop.GetTabAlignment() || shouldBreakLayouting
                     || childRenderers.Count - 1 == childPos || childRenderers[childPos + 1] is TabRenderer)) {
                    IRenderer tabRenderer = childRenderers[lastTabIndex];
                    IList<IRenderer> affectedRenderers = new List<IRenderer>();
                    affectedRenderers.AddAll(childRenderers.SubList(lastTabIndex + 1, childPos + 1));
                    float tabWidth = CalculateTab(layoutBox, curWidth, hangingTabStop, affectedRenderers, tabRenderer);
                    tabRenderer.Layout(new LayoutContext(new LayoutArea(layoutContext.GetArea().GetPageNumber(), bbox)));
                    float sumOfAffectedRendererWidths = 0;
                    foreach (IRenderer renderer in affectedRenderers) {
                        renderer.GetOccupiedArea().GetBBox().MoveRight(tabWidth + sumOfAffectedRendererWidths);
                        sumOfAffectedRendererWidths += renderer.GetOccupiedArea().GetBBox().GetWidth();
                    }
                    if (childResult.GetSplitRenderer() != null) {
                        childResult.GetSplitRenderer().GetOccupiedArea().GetBBox().MoveRight(tabWidth + sumOfAffectedRendererWidths
                             - childResult.GetSplitRenderer().GetOccupiedArea().GetBBox().GetWidth());
                    }
                    float tabAndNextElemWidth = tabWidth + childResult.GetOccupiedArea().GetBBox().GetWidth();
                    if (hangingTabStop.GetTabAlignment() == TabAlignment.RIGHT && curWidth + tabAndNextElemWidth < hangingTabStop
                        .GetTabPosition()) {
                        curWidth = hangingTabStop.GetTabPosition();
                    }
                    else {
                        curWidth += tabAndNextElemWidth;
                    }
                    widthHandler.UpdateMinChildWidth(minChildWidth_1);
                    widthHandler.UpdateMaxChildWidth(tabWidth + maxChildWidth_1);
                    hangingTabStop = null;
                }
                else {
                    if (null == hangingTabStop) {
                        curWidth += childResult.GetOccupiedArea().GetBBox().GetWidth();
                        widthHandler.UpdateMinChildWidth(minChildWidth_1);
                        widthHandler.UpdateMaxChildWidth(maxChildWidth_1);
                    }
                }
                occupiedArea.SetBBox(new Rectangle(layoutBox.GetX(), layoutBox.GetY() + layoutBox.GetHeight() - maxHeight, 
                    curWidth, maxHeight));
                if (shouldBreakLayouting) {
                    LineRenderer[] split = Split();
                    split[0].childRenderers = new List<IRenderer>(childRenderers.SubList(0, childPos));
                    bool wordWasSplitAndItWillFitOntoNextLine = false;
                    if (childResult is TextLayoutResult && ((TextLayoutResult)childResult).IsWordHasBeenSplit()) {
                        LayoutResult newLayoutResult = childRenderer.Layout(new LayoutContext(new LayoutArea(layoutContext.GetArea
                            ().GetPageNumber(), layoutBox)));
                        if (newLayoutResult is TextLayoutResult && !((TextLayoutResult)newLayoutResult).IsWordHasBeenSplit()) {
                            wordWasSplitAndItWillFitOntoNextLine = true;
                        }
                    }
                    if (wordWasSplitAndItWillFitOntoNextLine) {
                        split[1].childRenderers.Add(childRenderer);
                        split[1].childRenderers.AddAll(childRenderers.SubList(childPos + 1, childRenderers.Count));
                    }
                    else {
                        if (childResult.GetStatus() == LayoutResult.PARTIAL || childResult.GetStatus() == LayoutResult.FULL) {
                            split[0].AddChild(childResult.GetSplitRenderer());
                            anythingPlaced = true;
                        }
                        if (null != childResult.GetOverflowRenderer()) {
                            split[1].childRenderers.Add(childResult.GetOverflowRenderer());
                        }
                        split[1].childRenderers.AddAll(childRenderers.SubList(childPos + 1, childRenderers.Count));
                    }
                    split[0].childRenderers.RemoveAll(overflowFloats);
                    split[1].childRenderers.AddAll(0, overflowFloats);
                    // no sense to process empty renderer
                    if (split[1].childRenderers.Count == 0) {
                        split[1] = null;
                    }
                    IRenderer causeOfNothing = childResult.GetStatus() == LayoutResult.NOTHING ? childResult.GetCauseOfNothing
                        () : childRenderer;
                    if (split[1] == null) {
                        result = new LineLayoutResult(LayoutResult.FULL, occupiedArea, split[0], split[1], causeOfNothing);
                    }
                    else {
                        if (anythingPlaced || floatsPlaced) {
                            result = new LineLayoutResult(LayoutResult.PARTIAL, occupiedArea, split[0], split[1], causeOfNothing);
                        }
                        else {
                            result = new LineLayoutResult(LayoutResult.NOTHING, null, split[0], split[1], causeOfNothing);
                        }
                    }
                    if (newLineOccurred) {
                        result.SetSplitForcedByNewline(true);
                    }
                    break;
                }
                else {
                    anythingPlaced = true;
                    childPos++;
                }
            }
            if (result == null) {
                if ((anythingPlaced || floatsPlaced) && overflowFloats.IsEmpty() || 0 == childRenderers.Count) {
                    result = new LineLayoutResult(LayoutResult.FULL, occupiedArea, null, null);
                }
                else {
                    if (overflowFloats.IsEmpty()) {
                        // all kids were some non-image and non-text kids (tab-stops?),
                        // but in this case, it should be okay to return FULL, as there is nothing to be placed
                        result = new LineLayoutResult(LayoutResult.FULL, occupiedArea, null, null);
                    }
                    else {
                        if (anythingPlaced || floatsPlaced) {
                            LineRenderer[] split = Split();
                            split[0].childRenderers.AddAll(childRenderers.SubList(0, childPos));
                            split[0].childRenderers.RemoveAll(overflowFloats);
                            // If result variable is null up until now but not everything was placed - there is no
                            // content overflow, only floats are overflowing.
                            split[1].childRenderers.AddAll(overflowFloats);
                            result = new LineLayoutResult(LayoutResult.PARTIAL, occupiedArea, split[0], split[1], null);
                        }
                        else {
                            result = new LineLayoutResult(LayoutResult.NOTHING, null, null, this, overflowFloats[0]);
                        }
                    }
                }
            }
            if (baseDirection != null && baseDirection != BaseDirection.NO_BIDI) {
                // TODO what about float inlines?
                IList<IRenderer> children = null;
                if (result.GetStatus() == LayoutResult.PARTIAL) {
                    children = result.GetSplitRenderer().GetChildRenderers();
                }
                else {
                    if (result.GetStatus() == LayoutResult.FULL) {
                        children = GetChildRenderers();
                    }
                }
                if (children != null) {
                    bool newLineFound = false;
                    IList<LineRenderer.RendererGlyph> lineGlyphs = new List<LineRenderer.RendererGlyph>();
                    foreach (IRenderer child in children) {
                        if (newLineFound) {
                            break;
                        }
                        if (child is TextRenderer) {
                            GlyphLine childLine = ((TextRenderer)child).line;
                            for (int i = childLine.start; i < childLine.end; i++) {
                                if (TextUtil.IsNewLine(childLine.Get(i))) {
                                    newLineFound = true;
                                    break;
                                }
                                lineGlyphs.Add(new LineRenderer.RendererGlyph(childLine.Get(i), (TextRenderer)child));
                            }
                        }
                    }
                    byte[] lineLevels = new byte[lineGlyphs.Count];
                    if (levels != null) {
                        System.Array.Copy(levels, 0, lineLevels, 0, lineGlyphs.Count);
                    }
                    int[] reorder = TypographyUtils.ReorderLine(lineGlyphs, lineLevels, levels);
                    if (reorder != null) {
                        children.Clear();
                        int pos = 0;
                        int initialPos = 0;
                        bool reversed = false;
                        int offset = 0;
                        while (pos < lineGlyphs.Count) {
                            IRenderer renderer = lineGlyphs[pos].renderer;
                            TextRenderer newRenderer = new TextRenderer((TextRenderer)renderer).RemoveReversedRanges();
                            children.Add(newRenderer);
                            newRenderer.line = new GlyphLine(newRenderer.line);
                            IList<Glyph> replacementGlyphs = new List<Glyph>();
                            while (pos < lineGlyphs.Count && lineGlyphs[pos].renderer == renderer) {
                                if (pos + 1 < lineGlyphs.Count) {
                                    if (reorder[pos] == reorder[pos + 1] + 1 && !TextUtil.IsSpaceOrWhitespace(lineGlyphs[pos + 1].glyph) && !TextUtil
                                        .IsSpaceOrWhitespace(lineGlyphs[pos].glyph)) {
                                        reversed = true;
                                    }
                                    else {
                                        if (reversed) {
                                            IList<int[]> reversedRange = newRenderer.InitReversedRanges();
                                            reversedRange.Add(new int[] { initialPos - offset, pos - offset });
                                            reversed = false;
                                        }
                                        initialPos = pos + 1;
                                    }
                                }
                                replacementGlyphs.Add(lineGlyphs[pos].glyph);
                                pos++;
                            }
                            if (reversed) {
                                IList<int[]> reversedRange = newRenderer.InitReversedRanges();
                                reversedRange.Add(new int[] { initialPos - offset, pos - 1 - offset });
                                reversed = false;
                                initialPos = pos;
                            }
                            offset = initialPos;
                            newRenderer.line.SetGlyphs(replacementGlyphs);
                        }
                        float currentXPos = occupiedArea.GetBBox().GetLeft();
                        foreach (IRenderer child in children) {
                            float currentWidth = ((TextRenderer)child).CalculateLineWidth();
                            float[] margins = ((TextRenderer)child).GetMargins();
                            currentWidth += margins[1] + margins[3];
                            ((TextRenderer)child).occupiedArea.GetBBox().SetX(currentXPos).SetWidth(currentWidth);
                            currentXPos += currentWidth;
                        }
                    }
                    if (result.GetStatus() == LayoutResult.PARTIAL) {
                        LineRenderer overflow = (LineRenderer)result.GetOverflowRenderer();
                        if (levels != null) {
                            overflow.levels = new byte[levels.Length - lineLevels.Length];
                            System.Array.Copy(levels, lineLevels.Length, overflow.levels, 0, overflow.levels.Length);
                            if (overflow.levels.Length == 0) {
                                overflow.levels = null;
                            }
                        }
                    }
                }
            }
            LineRenderer processed = result.GetStatus() == LayoutResult.FULL ? this : (LineRenderer)result.GetSplitRenderer
                ();
            if (anythingPlaced || floatsPlaced) {
                processed.AdjustChildrenYLine().TrimLast();
                result.SetMinMaxWidth(minMaxWidth);
            }
            return result;
        }

        public virtual float GetMaxAscent() {
            return maxAscent;
        }

        public virtual float GetMaxDescent() {
            return maxDescent;
        }

        public virtual float GetYLine() {
            return occupiedArea.GetBBox().GetY() - maxDescent;
        }

        public virtual float GetLeadingValue(Leading leading) {
            switch (leading.GetLeadingType()) {
                case Leading.FIXED: {
                    return leading.GetValue();
                }

                case Leading.MULTIPLIED: {
                    return occupiedArea.GetBBox().GetHeight() * leading.GetValue();
                }

                default: {
                    throw new InvalidOperationException();
                }
            }
        }

        public override IRenderer GetNextRenderer() {
            return new LineRenderer();
        }

        protected internal override float? GetFirstYLineRecursively() {
            return GetYLine();
        }

        public virtual void Justify(float width) {
            float ratio = (float)this.GetPropertyAsFloat(Property.SPACING_RATIO);
            float freeWidth = occupiedArea.GetBBox().GetX() + width - GetLastChildRenderer().GetOccupiedArea().GetBBox
                ().GetX() - GetLastChildRenderer().GetOccupiedArea().GetBBox().GetWidth();
            int numberOfSpaces = GetNumberOfSpaces();
            int baseCharsCount = BaseCharactersCount();
            float baseFactor = freeWidth / (ratio * numberOfSpaces + (1 - ratio) * (baseCharsCount - 1));
            float wordSpacing = ratio * baseFactor;
            float characterSpacing = (1 - ratio) * baseFactor;
            float lastRightPos = occupiedArea.GetBBox().GetX();
            for (int i = 0; i < childRenderers.Count; ++i) {
                IRenderer child = childRenderers[i];
                float childX = child.GetOccupiedArea().GetBBox().GetX();
                child.Move(lastRightPos - childX, 0);
                childX = lastRightPos;
                if (child is TextRenderer) {
                    float childHSCale = (float)((TextRenderer)child).GetPropertyAsFloat(Property.HORIZONTAL_SCALING, 1f);
                    child.SetProperty(Property.CHARACTER_SPACING, characterSpacing / childHSCale);
                    child.SetProperty(Property.WORD_SPACING, wordSpacing / childHSCale);
                    bool isLastTextRenderer = i + 1 == childRenderers.Count;
                    float widthAddition = (isLastTextRenderer ? (((TextRenderer)child).LineLength() - 1) : ((TextRenderer)child
                        ).LineLength()) * characterSpacing + wordSpacing * ((TextRenderer)child).GetNumberOfSpaces();
                    child.GetOccupiedArea().GetBBox().SetWidth(child.GetOccupiedArea().GetBBox().GetWidth() + widthAddition);
                }
                lastRightPos = childX + child.GetOccupiedArea().GetBBox().GetWidth();
            }
            GetOccupiedArea().GetBBox().SetWidth(width);
        }

        protected internal virtual int GetNumberOfSpaces() {
            int spaces = 0;
            foreach (IRenderer child in childRenderers) {
                if (child is TextRenderer) {
                    spaces += ((TextRenderer)child).GetNumberOfSpaces();
                }
            }
            return spaces;
        }

        /// <summary>Gets the total lengths of characters in this line.</summary>
        /// <remarks>
        /// Gets the total lengths of characters in this line. Other elements (images, tables) are not taken
        /// into account.
        /// </remarks>
        protected internal virtual int Length() {
            int length = 0;
            foreach (IRenderer child in childRenderers) {
                if (child is TextRenderer) {
                    length += ((TextRenderer)child).LineLength();
                }
            }
            return length;
        }

        /// <summary>Returns the number of base characters, i.e.</summary>
        /// <remarks>Returns the number of base characters, i.e. non-mark characters</remarks>
        protected internal virtual int BaseCharactersCount() {
            int count = 0;
            foreach (IRenderer child in childRenderers) {
                if (child is TextRenderer) {
                    count += ((TextRenderer)child).BaseCharactersCount();
                }
            }
            return count;
        }

        public override String ToString() {
            StringBuilder sb = new StringBuilder();
            foreach (IRenderer renderer in childRenderers) {
                sb.Append(renderer.ToString());
            }
            return sb.ToString();
        }

        protected internal virtual LineRenderer CreateSplitRenderer() {
            return (LineRenderer)GetNextRenderer();
        }

        protected internal virtual LineRenderer CreateOverflowRenderer() {
            return (LineRenderer)GetNextRenderer();
        }

        protected internal virtual LineRenderer[] Split() {
            LineRenderer splitRenderer = CreateSplitRenderer();
            splitRenderer.occupiedArea = occupiedArea.Clone();
            splitRenderer.parent = parent;
            splitRenderer.maxAscent = maxAscent;
            splitRenderer.maxDescent = maxDescent;
            splitRenderer.levels = levels;
            splitRenderer.AddAllProperties(GetOwnProperties());
            LineRenderer overflowRenderer = CreateOverflowRenderer();
            overflowRenderer.parent = parent;
            overflowRenderer.AddAllProperties(GetOwnProperties());
            return new LineRenderer[] { splitRenderer, overflowRenderer };
        }

        protected internal virtual LineRenderer AdjustChildrenYLine() {
            float actualYLine = occupiedArea.GetBBox().GetY() + occupiedArea.GetBBox().GetHeight() - maxAscent;
            foreach (IRenderer renderer in childRenderers) {
                if (FloatingHelper.IsRendererFloating(renderer)) {
                    continue;
                }
                if (renderer is ILeafElementRenderer) {
                    float descent = ((ILeafElementRenderer)renderer).GetDescent();
                    renderer.Move(0, actualYLine - renderer.GetOccupiedArea().GetBBox().GetBottom() + descent);
                }
                else {
                    renderer.Move(0, occupiedArea.GetBBox().GetY() - renderer.GetOccupiedArea().GetBBox().GetBottom());
                }
            }
            return this;
        }

        protected internal virtual void ApplyLeading(float deltaY) {
            occupiedArea.GetBBox().MoveUp(deltaY);
            foreach (IRenderer child in childRenderers) {
                if (!FloatingHelper.IsRendererFloating(child)) {
                    child.Move(0, deltaY);
                }
            }
        }

        // TODO for floats we don't apply any leading for the moment (and therefore line-height for pdf2html is not entirely supported in terms of floats)
        protected internal virtual LineRenderer TrimLast() {
            int lastIndex = childRenderers.Count;
            IRenderer lastRenderer = null;
            while (--lastIndex >= 0) {
                lastRenderer = childRenderers[lastIndex];
                if (!FloatingHelper.IsRendererFloating(lastRenderer)) {
                    break;
                }
            }
            if (lastRenderer is TextRenderer && lastIndex >= 0) {
                float trimmedSpace = ((TextRenderer)lastRenderer).TrimLast();
                occupiedArea.GetBBox().SetWidth(occupiedArea.GetBBox().GetWidth() - trimmedSpace);
            }
            return this;
        }

        public virtual bool ContainsImage() {
            foreach (IRenderer renderer in childRenderers) {
                if (renderer is ImageRenderer) {
                    return true;
                }
            }
            return false;
        }

        protected internal override MinMaxWidth GetMinMaxWidth(float availableWidth) {
            LineLayoutResult result = (LineLayoutResult)((LineLayoutResult)Layout(new LayoutContext(new LayoutArea(1, 
                new Rectangle(availableWidth, AbstractRenderer.INF)))));
            return result.GetNotNullMinMaxWidth(availableWidth);
        }

        private LineRenderer[] SplitNotFittingFloat(int childPos, LayoutResult childResult) {
            LineRenderer[] split = Split();
            split[0].childRenderers.AddAll(childRenderers.SubList(0, childPos));
            split[0].childRenderers.Add(childResult.GetSplitRenderer());
            split[1].childRenderers.Add(childResult.GetOverflowRenderer());
            split[1].childRenderers.AddAll(childRenderers.SubList(childPos + 1, childRenderers.Count));
            return split;
        }

        private void AdjustLineOnFloatPlaced(Rectangle layoutBox, int childPos, FloatPropertyValue? kidFloatPropertyVal
            , Rectangle justPlacedFloatBox) {
            if (justPlacedFloatBox.GetBottom() >= layoutBox.GetTop() || justPlacedFloatBox.GetTop() < layoutBox.GetTop
                ()) {
                return;
            }
            bool ltr = true;
            // TODO handle it
            float floatWidth = justPlacedFloatBox.GetWidth();
            if (kidFloatPropertyVal.Equals(FloatPropertyValue.LEFT)) {
                layoutBox.SetWidth(layoutBox.GetWidth() - floatWidth).MoveRight(floatWidth);
                occupiedArea.GetBBox().MoveRight(floatWidth);
                if (ltr) {
                    for (int i = 0; i < childPos; ++i) {
                        IRenderer prevChild = childRenderers[i];
                        if (!FloatingHelper.IsRendererFloating(prevChild)) {
                            prevChild.GetOccupiedArea().GetBBox().MoveRight(floatWidth);
                        }
                    }
                }
            }
            else {
                layoutBox.SetWidth(layoutBox.GetWidth() - floatWidth);
                if (!ltr) {
                }
            }
        }

        // TODO
        private IRenderer GetLastChildRenderer() {
            return childRenderers[childRenderers.Count - 1];
        }

        private TabStop GetNextTabStop(float curWidth) {
            SortedDictionary<float, TabStop> tabStops = this.GetProperty<SortedDictionary<float, TabStop>>(Property.TAB_STOPS
                );
            KeyValuePair<float, TabStop>? nextTabStopEntry = null;
            TabStop nextTabStop = null;
            if (tabStops != null) {
                nextTabStopEntry = tabStops.HigherEntry(curWidth);
            }
            if (nextTabStopEntry != null) {
                nextTabStop = ((KeyValuePair<float, TabStop>)nextTabStopEntry).Value;
            }
            return nextTabStop;
        }

        /// <summary>Calculates and sets encountered tab size.</summary>
        /// <remarks>
        /// Calculates and sets encountered tab size.
        /// Returns null, if processing is finished and layout can be performed for the tab renderer;
        /// otherwise, in case when the tab should be processed after the next element in the line, this method returns corresponding tab stop.
        /// </remarks>
        private TabStop CalculateTab(IRenderer childRenderer, float curWidth, float lineWidth) {
            TabStop nextTabStop = GetNextTabStop(curWidth);
            if (nextTabStop == null) {
                ProcessDefaultTab(childRenderer, curWidth, lineWidth);
                return null;
            }
            childRenderer.SetProperty(Property.TAB_LEADER, nextTabStop.GetTabLeader());
            childRenderer.SetProperty(Property.WIDTH, UnitValue.CreatePointValue(nextTabStop.GetTabPosition() - curWidth
                ));
            childRenderer.SetProperty(Property.MIN_HEIGHT, maxAscent - maxDescent);
            if (nextTabStop.GetTabAlignment() == TabAlignment.LEFT) {
                return null;
            }
            return nextTabStop;
        }

        /// <summary>Calculates and sets tab size with the account of the element that is next in the line after the tab.
        ///     </summary>
        /// <remarks>
        /// Calculates and sets tab size with the account of the element that is next in the line after the tab.
        /// Returns resulting width of the tab.
        /// </remarks>
        private float CalculateTab(Rectangle layoutBox, float curWidth, TabStop tabStop, IList<IRenderer> affectedRenderers
            , IRenderer tabRenderer) {
            float sumOfAffectedRendererWidths = 0;
            foreach (IRenderer renderer in affectedRenderers) {
                sumOfAffectedRendererWidths += renderer.GetOccupiedArea().GetBBox().GetWidth();
            }
            float tabWidth = 0;
            switch (tabStop.GetTabAlignment()) {
                case TabAlignment.RIGHT: {
                    tabWidth = tabStop.GetTabPosition() - curWidth - sumOfAffectedRendererWidths;
                    break;
                }

                case TabAlignment.CENTER: {
                    tabWidth = tabStop.GetTabPosition() - curWidth - sumOfAffectedRendererWidths / 2;
                    break;
                }

                case TabAlignment.ANCHOR: {
                    float anchorPosition = -1;
                    float processedRenderersWidth = 0;
                    foreach (IRenderer renderer in affectedRenderers) {
                        anchorPosition = ((TextRenderer)renderer).GetTabAnchorCharacterPosition();
                        if (-1 != anchorPosition) {
                            break;
                        }
                        else {
                            processedRenderersWidth += renderer.GetOccupiedArea().GetBBox().GetWidth();
                        }
                    }
                    if (anchorPosition == -1) {
                        anchorPosition = 0;
                    }
                    tabWidth = tabStop.GetTabPosition() - curWidth - anchorPosition - processedRenderersWidth;
                    break;
                }
            }
            if (tabWidth < 0) {
                tabWidth = 0;
            }
            if (curWidth + tabWidth + sumOfAffectedRendererWidths > layoutBox.GetWidth()) {
                tabWidth -= (curWidth + sumOfAffectedRendererWidths + tabWidth) - layoutBox.GetWidth();
            }
            tabRenderer.SetProperty(Property.WIDTH, UnitValue.CreatePointValue(tabWidth));
            tabRenderer.SetProperty(Property.MIN_HEIGHT, maxAscent - maxDescent);
            return tabWidth;
        }

        private void ProcessDefaultTab(IRenderer tabRenderer, float curWidth, float lineWidth) {
            float? tabDefault = this.GetPropertyAsFloat(Property.TAB_DEFAULT);
            float? tabWidth = tabDefault - curWidth % tabDefault;
            if (curWidth + tabWidth > lineWidth) {
                tabWidth = lineWidth - curWidth;
            }
            tabRenderer.SetProperty(Property.WIDTH, UnitValue.CreatePointValue((float)tabWidth));
            tabRenderer.SetProperty(Property.MIN_HEIGHT, maxAscent - maxDescent);
        }

        private void UpdateChildrenParent() {
            foreach (IRenderer renderer in childRenderers) {
                renderer.SetParent(this);
            }
        }

        /// <summary>Trim first child text renderers.</summary>
        /// <returns>total number of trimmed glyphs.</returns>
        private int TrimFirst() {
            int totalNumberOfTrimmedGlyphs = 0;
            foreach (IRenderer renderer in childRenderers) {
                if (FloatingHelper.IsRendererFloating(renderer)) {
                    continue;
                }
                if (renderer is TextRenderer) {
                    TextRenderer textRenderer = (TextRenderer)renderer;
                    GlyphLine currentText = textRenderer.GetText();
                    if (currentText != null) {
                        int prevTextStart = currentText.start;
                        textRenderer.TrimFirst();
                        int numOfTrimmedGlyphs = textRenderer.GetText().start - prevTextStart;
                        totalNumberOfTrimmedGlyphs += numOfTrimmedGlyphs;
                    }
                    if (textRenderer.Length() > 0) {
                        break;
                    }
                }
                else {
                    break;
                }
            }
            return totalNumberOfTrimmedGlyphs;
        }

        /// <summary>Apply OTF features and return the last(!) base direction of child renderer</summary>
        /// <returns>the last(!) base direction of child renderer.</returns>
        private BaseDirection? ApplyOtf() {
            BaseDirection? baseDirection = this.GetProperty<BaseDirection?>(Property.BASE_DIRECTION);
            foreach (IRenderer renderer in childRenderers) {
                if (renderer is TextRenderer) {
                    ((TextRenderer)renderer).ApplyOtf();
                    if (baseDirection == null || baseDirection == BaseDirection.NO_BIDI) {
                        baseDirection = renderer.GetOwnProperty<BaseDirection?>(Property.BASE_DIRECTION);
                    }
                }
            }
            return baseDirection;
        }

        private void UpdateBidiLevels(int totalNumberOfTrimmedGlyphs, BaseDirection? baseDirection) {
            if (totalNumberOfTrimmedGlyphs != 0 && levels != null) {
                levels = iText.IO.Util.JavaUtil.ArraysCopyOfRange(levels, totalNumberOfTrimmedGlyphs, levels.Length);
            }
            IList<int> unicodeIdsReorderingList = null;
            if (levels == null && baseDirection != null && baseDirection != BaseDirection.NO_BIDI) {
                unicodeIdsReorderingList = new List<int>();
                bool newLineFound = false;
                foreach (IRenderer child in childRenderers) {
                    if (newLineFound) {
                        break;
                    }
                    if (child is TextRenderer) {
                        GlyphLine text = ((TextRenderer)child).GetText();
                        for (int i = text.start; i < text.end; i++) {
                            Glyph glyph = text.Get(i);
                            if (TextUtil.IsNewLine(glyph)) {
                                newLineFound = true;
                                break;
                            }
                            // we assume all the chars will have the same bidi group
                            // we also assume pairing symbols won't get merged with other ones
                            int unicode = glyph.HasValidUnicode() ? glyph.GetUnicode() : glyph.GetUnicodeChars()[0];
                            unicodeIdsReorderingList.Add(unicode);
                        }
                    }
                }
                levels = unicodeIdsReorderingList.Count > 0 ? TypographyUtils.GetBidiLevels(baseDirection, ArrayUtil.ToArray
                    (unicodeIdsReorderingList)) : null;
            }
        }

        /// <summary>While resolving TextRenderer may split into several ones with different fonts.</summary>
        private void ResolveChildrenFonts() {
            IList<IRenderer> newChildRenderers = new List<IRenderer>(childRenderers.Count);
            bool updateChildRendrers = false;
            foreach (IRenderer child in childRenderers) {
                if (child is TextRenderer) {
                    if (((TextRenderer)child).ResolveFonts(newChildRenderers)) {
                        updateChildRendrers = true;
                    }
                }
                else {
                    newChildRenderers.Add(child);
                }
            }
            // this mean, that some TextRenderer has been replaced.
            if (updateChildRendrers) {
                childRenderers = newChildRenderers;
            }
        }

        internal class RendererGlyph {
            public RendererGlyph(Glyph glyph, TextRenderer textRenderer) {
                this.glyph = glyph;
                this.renderer = textRenderer;
            }

            public Glyph glyph;

            public TextRenderer renderer;
        }
    }
}
