// 
// QuickFixEditorExtension.cs
//  
// Author:
//       Mike Krüger <mkrueger@novell.com>
// 
// Copyright (c) 2011 Novell, Inc (http://www.novell.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using MonoDevelop.Ide.Gui.Content;
using Gtk;
using Mono.TextEditor;
using System.Collections.Generic;
using MonoDevelop.Components.Commands;
using MonoDevelop.SourceEditor.QuickTasks;
using System.Linq;
using MonoDevelop.Refactoring;
using ICSharpCode.NRefactory;
using System.Threading;
using MonoDevelop.Core;
using ICSharpCode.NRefactory.CSharp;

namespace MonoDevelop.CodeActions
{
	class CodeActionEditorExtension : TextEditorExtension 
	{
		CodeActionWidget widget;
		uint quickFixTimeout;
		
		public IEnumerable<CodeAction> Fixes {
			get;
			private set;
		}
		
		void RemoveWidget ()
		{
			if (widget != null) {
				widget.Destroy ();
				widget = null;
			}
			if (currentSmartTag != null) {
				bool wasRemoved = document.Editor.Document.RemoveMarker (currentSmartTag);
				if (!wasRemoved) {
					LoggingService.LogWarning ("Can't remove smart tag marker from document.");
				}
				currentSmartTag = null;
			}
		}
		
		public override void Dispose ()
		{
			CancelQuickFixTimer ();
			document.Editor.SelectionChanged -= HandleSelectionChanged;
			document.DocumentParsed -= HandleDocumentDocumentParsed;
			RemoveWidget ();
			base.Dispose ();
		}
		TextLocation loc;
		void CreateWidget (IEnumerable<CodeAction> fixes, TextLocation loc)
		{
			this.loc = loc;
			var editor = document.Editor;
			var container = editor.Parent;
			var point = editor.Parent.LocationToPoint (loc);
			point.Y += (int)editor.LineHeight;
			if (widget == null) {
				widget = new CodeActionWidget (this, Document);
				container.AddTopLevelWidget (
					widget,
					point.X,
					point.Y
				);
				widget.Show ();
			} else {
				if (!widget.Visible)
					widget.Show ();
				container.MoveTopLevelWidget (
					widget,
					point.X,
					point.Y
				);
			}
			widget.SetFixes (fixes, loc);
		}

		public void CancelQuickFixTimer ()
		{
			if (quickFixCancellationTokenSource != null)
				quickFixCancellationTokenSource.Cancel ();
			if (quickFixTimeout != 0) {
				GLib.Source.Remove (quickFixTimeout);
				quickFixTimeout = 0;
			}
		}

		CancellationTokenSource quickFixCancellationTokenSource;

		public override void CursorPositionChanged ()
		{
			CancelQuickFixTimer ();
			if (QuickTaskStrip.EnableFancyFeatures &&  Document.ParsedDocument != null && !Debugger.DebuggingService.IsDebugging) {
				quickFixCancellationTokenSource = new CancellationTokenSource ();
				var token = quickFixCancellationTokenSource.Token;
				quickFixTimeout = GLib.Timeout.Add (100, delegate {
					var loc = Document.Editor.Caret.Location;
					RefactoringService.QueueQuickFixAnalysis (Document, loc, token, delegate(List<CodeAction> fixes) {
						if (!fixes.Any ()) {
							ICSharpCode.NRefactory.Semantics.ResolveResult resolveResult;
							ICSharpCode.NRefactory.CSharp.AstNode node;
							if (ResolveCommandHandler.ResolveAt (document, out resolveResult, out node, token)) {
								var possibleNamespaces = ResolveCommandHandler.GetPossibleNamespaces (document, node, ref resolveResult);
								if (!possibleNamespaces.Any ()) {
									if (currentSmartTag != null)
										Application.Invoke (delegate { RemoveWidget (); });
									return;
								}
							} else {
								if (currentSmartTag != null)
									Application.Invoke (delegate { RemoveWidget (); });
								return;
							}
						}
						Application.Invoke (delegate {
							if (token.IsCancellationRequested)
								return;
							CreateSmartTag (fixes, loc);
							quickFixTimeout = 0;
						});
					});
					return false;
				});
			} else {
				RemoveWidget ();
			}
			base.CursorPositionChanged ();
		}

		class SmartTagMarker : TextSegmentMarker, IActionTextLineMarker
		{
			CodeActionEditorExtension codeActionEditorExtension;
			List<CodeAction> fixes;
			DocumentLocation loc;

			public SmartTagMarker (int offset, CodeActionEditorExtension codeActionEditorExtension, List<CodeAction> fixes, DocumentLocation loc) : base (offset, 0)
			{
				this.codeActionEditorExtension = codeActionEditorExtension;
				this.fixes = fixes;
				this.loc = loc;
			}

			public SmartTagMarker (int offset) : base (offset, 0)
			{
			}

			public override void Draw (TextEditor editor, Cairo.Context cr, Pango.Layout layout, bool selected, int startOffset, int endOffset, double y, double startXPos, double endXPos)
			{
				int column = Offset - startOffset;

				var pos = layout.IndexToPos (column).X / Pango.Scale.PangoScale;

				cr.Rectangle (Math.Floor (startXPos + pos) + 0.5, Math.Floor (y + editor.LineHeight - 4 * cr.LineWidth) + 0.5, 8 * cr.LineWidth, 2 * cr.LineWidth);

				if (HslColor.Brightness (editor.ColorStyle.PlainText.Background) < 0.5) {
					cr.Color = new Cairo.Color (0.8, 0.8, 1);
				} else {
					cr.Color = new Cairo.Color (0.2, 0.2, 1);
				}
				cr.Stroke ();
			}

			#region IActionTextLineMarker implementation

			bool IActionTextLineMarker.MousePressed (TextEditor editor, MarginMouseEventArgs args)
			{
				return false;
			}

			void IActionTextLineMarker.MouseHover (TextEditor editor, MarginMouseEventArgs args, TextLineMarkerHoverResult result)
			{
				var line = editor.GetLineByOffset (Offset);
				var y = editor.LineToY (line.LineNumber);

				var x = editor.ColumnToX (line, Offset - line.Offset + 1);
				if (args.X - x >= -8 * editor.Options.Zoom && args.X - x < 8 * editor.Options.Zoom && args.Y > y + editor.LineHeight / 2) {
					Popup ();
				}
			}

			public void Popup ()
			{
				codeActionEditorExtension.CreateWidget (fixes, loc);
				codeActionEditorExtension.widget.PopupQuickFixMenu ();
				codeActionEditorExtension.widget.Destroy ();
			}
			#endregion
		}

		SmartTagMarker currentSmartTag;
		void CreateSmartTag (List<CodeAction> fixes, DocumentLocation loc)
		{
			Fixes = fixes;
			if (!QuickTaskStrip.EnableFancyFeatures) {
				RemoveWidget ();
				return;
			}
			var editor = document.Editor;
			if (editor == null || editor.Parent == null || !editor.Parent.IsRealized) {
				RemoveWidget ();
				return;
			}
			if (document.ParsedDocument == null || document.ParsedDocument.IsInvalid) {
				RemoveWidget ();
				return;
			}

			var container = editor.Parent;
			if (container == null) {
				RemoveWidget ();
				return;
			}
			bool first = true;
			DocumentLocation smartTagLoc = loc;
			foreach (var fix in fixes) {
				if (fix.DocumentRegion.IsEmpty)
					continue;
				if (first || loc < fix.DocumentRegion.Begin)
					smartTagLoc = fix.DocumentRegion.Begin;
				first = false;
			}
			if (smartTagLoc.Line != loc.Line)
				smartTagLoc = new DocumentLocation (loc.Line, 1);


			// got no fix location -> try to search word start
			if (first) {
				int offset = document.Editor.LocationToOffset (smartTagLoc);
				while (offset > 0) {
					char ch = document.Editor.GetCharAt (offset - 1);
					if (!char.IsLetterOrDigit (ch) && ch != '_')
						break;
					offset--;
				}
				smartTagLoc = document.Editor.OffsetToLocation (offset);
			}
			RemoveWidget ();
			currentSmartTag = new SmartTagMarker (document.Editor.LocationToOffset (smartTagLoc), this, fixes, smartTagLoc);
			document.Editor.Document.AddMarker (currentSmartTag);
		}
		
		public override void Initialize ()
		{
			base.Initialize ();
			document.DocumentParsed += HandleDocumentDocumentParsed;
			document.Editor.SelectionChanged += HandleSelectionChanged;
		}

		void HandleSelectionChanged (object sender, EventArgs e)
		{
			CursorPositionChanged ();
		}
		
		void HandleDocumentDocumentParsed (object sender, EventArgs e)
		{
			CursorPositionChanged ();
		}
		
		[CommandUpdateHandler(RefactoryCommands.QuickFix)]
		public void UpdateQuickFixCommand (CommandInfo ci)
		{
			if (QuickTaskStrip.EnableFancyFeatures) {
				ci.Enabled = currentSmartTag != null;
			} else {
				ci.Enabled = true;
			}
		}
		
		[CommandHandler(RefactoryCommands.QuickFix)]
		void OnQuickFixCommand ()
		{
			if (!QuickTaskStrip.EnableFancyFeatures) {
				var w = new CodeActionWidget (this, Document);
				w.SetFixes (RefactoringService.GetValidActions (Document, Document.Editor.Caret.Location).Result, Document.Editor.Caret.Location);
				w.PopupQuickFixMenu ();
				w.Destroy ();
				return;
			}
			if (currentSmartTag == null)
				return;
			if (widget == null || !widget.Visible)
				currentSmartTag.Popup ();
			widget.PopupQuickFixMenu ();
		}
	}
}

