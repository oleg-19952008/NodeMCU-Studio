﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using FarsiLibrary.Win;
using FastColoredTextBoxNS;
using NodeMCU_Studio_2016.Properties;
using MessageBox = System.Windows.Forms.MessageBox;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Style = FastColoredTextBoxNS.Style;

namespace NodeMCU_Studio_2016
{
    public partial class PowerfulLuaEditor : Form
    {
        readonly List<string> _keywords = new List<string>();
        readonly List<string> _methods = new List<string>();
        readonly List<string> _snippets = new List<string>();
        readonly List<string> _declarationSnippets = new List<string>();

        readonly Style _invisibleCharsStyle = new InvisibleCharsRenderer(Pens.Gray);
        readonly Color _currentLineColor = Color.FromArgb(100, 210, 210, 255);
        readonly Color _changedLineColor = Color.FromArgb(255, 230, 230, 255);
        readonly SynchronizationContext _context;

        readonly Window _parent;

        public PowerfulLuaEditor(Window parent)
        {
            InitializeComponent();

            //init menu images
            var resources = new ComponentResourceManager(typeof(PowerfulLuaEditor));
            copyToolStripMenuItem.Image = ((Image)(resources.GetObject("copyToolStripButton.Image")));
            cutToolStripMenuItem.Image = ((Image)(resources.GetObject("cutToolStripButton.Image")));
            pasteToolStripMenuItem.Image = ((Image)(resources.GetObject("pasteToolStripButton.Image")));

            ByteArrayToList(Resources.keywords, _keywords);
            ByteArrayToList(Resources.methods, _methods);
            ByteArrayToList(Resources.snippets, _snippets);
            ByteArrayToList(Resources.declarationSnippets, _declarationSnippets);

            _parent = parent;

            _context = SynchronizationContext.Current;

            RefreshSerialPort();

            SerialPort.GetInstance().IsOpenChanged += PowerfulLuaEditor_IsOpenChanged;
            PowerfulLuaEditor_IsOpenChanged(false);

            SerialPort.GetInstance().IsWorkingChanged += delegate(bool isWorking)
            {
                _context.Post(_ =>
                {
                    toolStripDownloadButton.Enabled = !isWorking;
                    toolStripUploadButton.Enabled = !isWorking;
                    toolStripRunButton.Enabled = !isWorking;
                    toolStripCloseButton.Enabled = !isWorking;
                    textBoxCommand.Enabled = !isWorking;
                }, null);
            };

            SerialPort.GetInstance().OnDataReceived += delegate (string s)
            {
                _context.Post(_ =>
                {
                    textBoxConsole.AppendText(s);
                    textBoxConsole.Navigate(textBoxConsole.Lines.Count - 1);
                }, null);
            };

            textBoxCommand.AutoCompleteSource = AutoCompleteSource.CustomSource;
            textBoxCommand.AutoCompleteCustomSource = new AutoCompleteStringCollection();
            _methods.ForEach(str => textBoxCommand.AutoCompleteCustomSource.Add(str));
        }

        private void PowerfulLuaEditor_IsOpenChanged(bool isOpen)
        {
            _context.Post(_ =>
            {
                closeSerialPortConnectionToolStripMenuItem.Enabled = isOpen;
                toolStripCloseButton.Enabled = isOpen;
            }, null);
            
        }

        private static void ByteArrayToList(byte[] array, ICollection<string> list)
        {
            using (var sr = new StreamReader(new MemoryStream(array)))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    list.Add(Regex.Unescape(line));
                }
            }
        }

        private void newToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CreateTab(null);
        }

        private readonly Style _sameWordsStyle = new MarkerStyle(new SolidBrush(Color.FromArgb(50, Color.Gray)));

        private void CreateTab(string fileName)
        {
            try
            {
                var tb = new FastColoredTextBox
                {
                    Font = new Font("Segoe Script", 12f),
                    ContextMenuStrip = cmMain,
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.Fixed3D,
                    LeftPadding = 17,
                    ImeMode = System.Windows.Forms.ImeMode.On,
                Language = Language.Lua
                };
                //tb.VirtualSpace = true;
                tb.AddStyle(_sameWordsStyle);//same words style
                var tab = new FATabStripItem(fileName != null ? Path.GetFileName(fileName) : "[new]", tb)
                {
                    Tag = fileName
                };
                if (fileName != null)
                    tb.OpenFile(fileName,System.Text.Encoding.UTF8);
                tb.Tag = new TbInfo();
                tsFiles.AddTab(tab);
                tsFiles.SelectedItem = tab;
                tb.Focus();
                tb.DelayedTextChangedInterval = 1000;
                tb.DelayedEventsInterval = 500;
                tb.TextChangedDelayed += tb_TextChangedDelayed;
                tb.SelectionChangedDelayed += tb_SelectionChangedDelayed;
                tb.KeyDown += tb_KeyDown;
                tb.MouseMove += tb_MouseMove;
                tb.ChangedLineColor = _changedLineColor;
                if(btHighlightCurrentLine.Checked)
                    tb.CurrentLineColor = _currentLineColor;
                tb.ShowFoldingLines = btShowFoldingLines.Checked;
                tb.HighlightingRangeType = HighlightingRangeType.VisibleRange;
                //create autocomplete popup menu
                var popupMenu = new AutocompleteMenu(tb);
                popupMenu.Items.ImageList = ilAutocomplete;
                popupMenu.Opening += popupMenu_Opening;
                BuildAutocompleteMenu(popupMenu);
                var tbInfo = tb.Tag as TbInfo;
                if (tbInfo != null) tbInfo.PopupMenu = popupMenu;
                tb.KeyDown += OnKeyDown;
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(ex.Message, Resources.error, MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry)
                    CreateTab(fileName);
            }
        }

        void popupMenu_Opening(object sender, CancelEventArgs e)
        {
            //---block autocomplete menu for comments
            //get index of green style (used for comments)
            var iGreenStyle = CurrentTb.GetStyleIndex(CurrentTb.SyntaxHighlighter.GreenStyle);
            if (iGreenStyle >= 0)
                if (CurrentTb.Selection.Start.iChar > 0)
                {
                    //current char (before caret)
                    var c = CurrentTb[CurrentTb.Selection.Start.iLine][CurrentTb.Selection.Start.iChar - 1];
                    //green Style
                    var greenStyleIndex = Range.ToStyleIndex(iGreenStyle);
                    //if char contains green style then block popup menu
                    if ((c.style & greenStyleIndex) != 0)
                        e.Cancel = true;
                }
        }

        private void BuildAutocompleteMenu(AutocompleteMenu popupMenu)
        {
            List<AutocompleteItem> items = _snippets.Select(item => new SnippetAutocompleteItem(item) {ImageIndex = 1}).Cast<AutocompleteItem>().ToList();
            items.AddRange(_declarationSnippets.Select(item => new DeclarationSnippet(item) {ImageIndex = 0}));
            items.AddRange(_methods.Select(item => new MethodAutocompleteItem(item) {ImageIndex = 2}));
            items.AddRange(_keywords.Select(item => new AutocompleteItem(item)));

            items.Add(new InsertSpaceSnippet());
            items.Add(new InsertSpaceSnippet(@"^(\w+)([=<>!:]+)(\w+)$"));
            items.Add(new InsertEnterSnippet());

            //set as autocomplete source
            popupMenu.Items.SetAutocompleteItems(items);
            popupMenu.SearchPattern = @"[\w\.:=!<>]";
        }

        void tb_MouseMove(object sender, MouseEventArgs e)
        {
            var tb = sender as FastColoredTextBox;
            if (tb != null)
            {
                var place = tb.PointToPlace(e.Location);
                var r = new Range(tb, place, place);

                string text = r.GetFragment("[a-zA-Z]").Text;
                lbWordUnderMouse.Text = text;
            }
        }

        void tb_KeyDown(object sender, KeyEventArgs e)
        {

        }

        void tb_SelectionChangedDelayed(object sender, EventArgs e)
        {
            var tb = sender as FastColoredTextBox;
            //remember last visit time
            if (tb != null && (tb.Selection.IsEmpty && tb.Selection.Start.iLine < tb.LinesCount))
            {
                if (_lastNavigatedDateTime != tb[tb.Selection.Start.iLine].LastVisit)
                {
                    tb[tb.Selection.Start.iLine].LastVisit = DateTime.Now;
                    _lastNavigatedDateTime = tb[tb.Selection.Start.iLine].LastVisit;
                }
            }

            //highlight same words
            if (tb != null)
            {
                tb.VisibleRange.ClearStyle(_sameWordsStyle);
                if (!tb.Selection.IsEmpty)
                    return;//user selected diapason
                //get fragment around caret
                var fragment = tb.Selection.GetFragment(@"\w");
                string text = fragment.Text;
                if (text.Length == 0)
                    return;
                //highlight same words
                Range[] ranges = tb.VisibleRange.GetRanges("\\b" + text + "\\b").ToArray();

                if (ranges.Length > 1)
                    foreach (var r in ranges)
                        r.SetStyle(_sameWordsStyle);
            }
        }

        void tb_TextChangedDelayed(object sender, TextChangedEventArgs e)
        {
            //rebuild object explorer
            var fastColoredTextBox = sender as FastColoredTextBox;
            if (fastColoredTextBox != null)
            {
                string text = fastColoredTextBox.Text;
                ThreadPool.QueueUserWorkItem(
                    o=>ReBuildObjectExplorer(text)
                    );
                ThreadPool.QueueUserWorkItem(
                    o => ReFoldLines()
                    );
            }

            //show invisible chars
            HighlightInvisibleChars(e.ChangedRange);
        }

        private void HighlightInvisibleChars(Range range)
        {
            range.ClearStyle(_invisibleCharsStyle);
            if (btInvisibleChars.Checked)
                range.SetStyle(_invisibleCharsStyle, @".$|.\r\n|\s");
        }

        List<ExplorerItem> _explorerList = new List<ExplorerItem>();

        private void ReFoldLines()
        {
            CurrentTb.Range.ClearFoldingMarkers();
            CurrentTb.Range.SetFoldingMarkers("(function|repeat|if|else|elseif|for|while|do)", "end");
        }

        private void ReBuildObjectExplorer(string text)
        {
            try
            {
                var list = new List<ExplorerItem>();
                //find classes, methods and properties
                //Regex regex = new Regex(@"^(?<range>[\w\s]+\b(class|struct|enum|interface)\s+[\w<>,\s]+)|^\s*(public|private|internal|protected)[^\n]+(\n?\s*{|;)?", RegexOptions.Multiline);
                var regex = new Regex(@"^\s*function\s+[^\s]+\(.*\)|^\s*local\s+[^\s]+", RegexOptions.Multiline);
                foreach (Match r in regex.Matches(text))
                    try
                    {
                        var s = r.Value;
                        var i = s.IndexOfAny(new[] {'=', '{', ';'});
                        if (i >= 0)
                            s = s.Substring(0, i);
                        s = s.Trim();

                        var item = new ExplorerItem {Title = s, Position = r.Index};
                        if (Regex.IsMatch(item.Title, @"\b(function)\b"))
                        {
                            var parts = item.Title.Split('(');
                            item.Title = parts[0].Substring(parts[0].LastIndexOf(' ')).Trim() + "(" + parts[1];
                            item.Type = ExplorerItemType.Method;
                        }
                        else
                        {
                            int ii = item.Title.LastIndexOf(' ');
                            if (ii != -1)
                            {
                                item.Title = item.Title.Substring(ii);
                            }
                            item.Title = item.Title.Trim();
                            item.Type = ExplorerItemType.Property;
                        }
                        list.Add(item);
                    }
                    catch(Exception e)
                    {
                        MessageBox.Show(e.ToString());
                    }

                list.Sort(new ExplorerItemComparer());

                BeginInvoke(
                    new Action(() =>
                        {
                            _explorerList = list;
                            dgvObjectExplorer.RowCount = _explorerList.Count;
                            dgvObjectExplorer.Invalidate();
                        })
                );
            }
            catch (Exception e)
            {
                //MessageBox.Show(e.ToString());
                // ignore me...
            }
        }

        enum ExplorerItemType
        {
            Class, Method, Property, Event
        }

        class ExplorerItem
        {
            public ExplorerItemType Type;
            public string Title;
            public int Position;
        }

        class ExplorerItemComparer : IComparer<ExplorerItem>
        {
            public int Compare(ExplorerItem x, ExplorerItem y)
            {
                return String.Compare(x.Title, y.Title, StringComparison.Ordinal);
            }
        }

        private void tsFiles_TabStripItemClosing(TabStripItemClosingEventArgs e)
        {
            var fastColoredTextBox = e.Item.Controls[0] as FastColoredTextBox;
            if (fastColoredTextBox != null && fastColoredTextBox.IsChanged)
            {
                switch(MessageBox.Show(string.Format(Resources.do_you_want_to_save, e.Item.Title), Resources.save, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information))
                {
                    case DialogResult.Yes:
                        if (!Save(e.Item))
                            e.Cancel = true;
                        break;
                    case DialogResult.Cancel:
                         e.Cancel = true;
                        break;
                }
            }
        }

        private bool Save(FATabStripItem tab)
        {
            var tb = (tab.Controls[0] as FastColoredTextBox);
            if (tab.Tag == null)
            {
                if (sfdMain.ShowDialog() != DialogResult.OK)
                    return false;
                tab.Title = Path.GetFileName(sfdMain.FileName);
                tab.Tag = sfdMain.FileName;
            }

            try
            {
                if (tb != null)
                {
                    File.WriteAllText((string) tab.Tag, tb.Text);
                    tb.IsChanged = false;
                }
            }
            catch (Exception ex)
            {
                return MessageBox.Show(ex.Message, Resources.error, MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry && Save(tab);
            }

            if (tb != null) tb.Invalidate();

            return true;
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (tsFiles.SelectedItem != null)
                Save(tsFiles.SelectedItem);
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (tsFiles.SelectedItem != null)
            {
                string oldFile = tsFiles.SelectedItem.Tag as string;
                tsFiles.SelectedItem.Tag = null;
                if (!Save(tsFiles.SelectedItem))
                if(oldFile!=null)
                {
                    tsFiles.SelectedItem.Tag = oldFile;
                    tsFiles.SelectedItem.Title = Path.GetFileName(oldFile);
                }
            }
        }

        private void quitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ofdMain.ShowDialog() == DialogResult.OK)
                CreateTab(ofdMain.FileName);
        }

        FastColoredTextBox CurrentTb
        {
            get
            {
                if (tsFiles.SelectedItem != null) return tsFiles.SelectedItem.Controls[0] as FastColoredTextBox;
                return null;
            }

            set
            {
                tsFiles.SelectedItem = (value.Parent as FATabStripItem);
                value.Focus();
            }
        }

        private void cutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.Cut();
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.Copy();
        }

        private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.Paste();
        }

        private void selectAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.Selection.SelectAll();
        }

        private void undoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CurrentTb.UndoEnabled)
                CurrentTb.Undo();
        }

        private void redoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CurrentTb.RedoEnabled)
                CurrentTb.Redo();
        }

        private void UpdateInterfaceHasFiles()
        {
            var tb = CurrentTb;
            undoStripButton.Enabled = undoToolStripMenuItem.Enabled = undoToolStripMenuItem1.Enabled = tb.UndoEnabled;
            redoStripButton.Enabled = redoToolStripMenuItem.Enabled = redoToolStripMenuItem1.Enabled = tb.RedoEnabled;
            saveToolStripButton.Enabled = saveToolStripMenuItem.Enabled = tb.IsChanged;
            saveAsToolStripMenuItem.Enabled = true;
            pasteToolStripButton.Enabled = pasteToolStripMenuItem.Enabled = pasteToolStripMenuItem1.Enabled = true;
            cutToolStripButton.Enabled = cutToolStripMenuItem.Enabled = cutToolStripMenuItem1.Enabled = 
            copyToolStripButton.Enabled = copyToolStripMenuItem.Enabled = copyToolStripMenuItem1.Enabled = !tb.Selection.IsEmpty;
            printToolStripButton.Enabled = true;
        }

        private void UpdateInterfaceNoFiles()
        {
            undoStripButton.Enabled = undoToolStripMenuItem.Enabled = undoToolStripMenuItem1.Enabled = false;
            redoStripButton.Enabled = redoToolStripMenuItem.Enabled = redoToolStripMenuItem1.Enabled = false;
            saveToolStripButton.Enabled = saveToolStripMenuItem.Enabled = false;
            saveAsToolStripMenuItem.Enabled = false;
            pasteToolStripButton.Enabled = pasteToolStripMenuItem.Enabled = pasteToolStripMenuItem1.Enabled = false;
            cutToolStripButton.Enabled = cutToolStripMenuItem.Enabled = cutToolStripMenuItem1.Enabled =
            copyToolStripButton.Enabled = copyToolStripMenuItem.Enabled = copyToolStripMenuItem1.Enabled = false;
            printToolStripButton.Enabled = false;
            dgvObjectExplorer.RowCount = 0;
        }

        private void tmUpdateInterface_Tick(object sender, EventArgs e)
        {
            try
            {
                if(CurrentTb != null && tsFiles.Items.Count>0)
                {
                    UpdateInterfaceHasFiles();
                }
                else
                {
                    UpdateInterfaceNoFiles();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void printToolStripButton_Click(object sender, EventArgs e)
        {
            if(CurrentTb!=null)
            {
                var settings = new PrintDialogSettings
                {
                    Title = tsFiles.SelectedItem.Title,
                    Header = "&b&w&b",
                    Footer = "&b&p"
                };
                CurrentTb.Print(settings);
            }
        }

        bool _tbFindChanged;

        private void tbFind_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r' && CurrentTb != null)
            {
                Range r = _tbFindChanged?CurrentTb.Range.Clone():CurrentTb.Selection.Clone();
                _tbFindChanged = false;
                r.End = new Place(CurrentTb[CurrentTb.LinesCount - 1].Count, CurrentTb.LinesCount - 1);
                var pattern = Regex.Escape(tbFind.Text);
                foreach (var found in r.GetRanges(pattern))
                {
                    found.Inverse();
                    CurrentTb.Selection = found;
                    CurrentTb.DoSelectionVisible();
                    return;
                }
                MessageBox.Show(Resources.not_found);
            }
            else
                _tbFindChanged = true;
        }

        private void findToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.ShowFindDialog();
        }

        private void replaceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.ShowReplaceDialog();
        }

        private void PowerfulLuaEditor_FormClosing(object sender, FormClosingEventArgs e)
        {
            List<FATabStripItem> list = tsFiles.Items.Cast<FATabStripItem>().ToList();
            foreach (var tab in list)
            {
                TabStripItemClosingEventArgs args = new TabStripItemClosingEventArgs(tab);
                tsFiles_TabStripItemClosing(args);
                if (args.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                tsFiles.RemoveTab(tab);
            }

            _parent.Close();
        }

        private void dgvObjectExplorer_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (CurrentTb == null || e.RowIndex >= _explorerList.Count) return;

            var item = _explorerList[e.RowIndex];
            CurrentTb.GoEnd();
            CurrentTb.SelectionStart = item.Position;
            CurrentTb.DoSelectionVisible();
            CurrentTb.Focus();
        }

        private void dgvObjectExplorer_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            try
            {
                if (e.RowIndex >= _explorerList.Count) return;
                var item = _explorerList[e.RowIndex];

                if (e.ColumnIndex == 1)
                    e.Value = item.Title;
                else
                    switch (item.Type)
                    {
                        case ExplorerItemType.Class:
                            e.Value = Resources.class_libraries;
                            return;
                        case ExplorerItemType.Method:
                            e.Value = Resources.box;
                            return;
                        case ExplorerItemType.Event:
                            e.Value = Resources.lightning;
                            return;
                        case ExplorerItemType.Property:
                            e.Value = Resources.property;
                            return;
                    }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.ToString());
            }
        }

        private void tsFiles_TabStripItemSelectionChanged(TabStripItemChangedEventArgs e)
        {
            if (CurrentTb != null)
            {
                CurrentTb.Focus();
                string text = CurrentTb.Text;
                ThreadPool.QueueUserWorkItem(
                    o => ReBuildObjectExplorer(text)
                );
                ThreadPool.QueueUserWorkItem(
                    o => ReFoldLines()
                );
                CurrentTb.Invalidate();
            }
        }

        private void backStripButton_Click(object sender, EventArgs e)
        {
            NavigateBackward();
        }

        private void forwardStripButton_Click(object sender, EventArgs e)
        {
            NavigateForward();
        }

        DateTime _lastNavigatedDateTime = DateTime.Now;

        private void NavigateBackward()
        {
            DateTime max = new DateTime();
            int iLine = -1;
            FastColoredTextBox tb = null;
            for (int iTab = 0; iTab < tsFiles.Items.Count; iTab++)
            {
                var t = (tsFiles.Items[iTab].Controls[0] as FastColoredTextBox);
                if (t != null)
                {
                    for (int i = 0; i < t.LinesCount; i++)
                        if (t[i].LastVisit < _lastNavigatedDateTime && t[i].LastVisit > max)
                        {
                            max = t[i].LastVisit;
                            iLine = i;
                            tb = t;
                        }
                }
            }
            if (iLine >= 0)
            {
                if (tb == null) return;
                tsFiles.SelectedItem = (tb.Parent as FATabStripItem);
                tb.Navigate(iLine);
                _lastNavigatedDateTime = tb[iLine].LastVisit;
                //Console.WriteLine("Backward: " + _lastNavigatedDateTime);
                tb.Focus();
                tb.Invalidate();
            }
        }

        private void NavigateForward()
        {
            DateTime min = DateTime.Now;
            int iLine = -1;
            FastColoredTextBox tb = null;
            for (int iTab = 0; iTab < tsFiles.Items.Count; iTab++)
            {
                var t = (tsFiles.Items[iTab].Controls[0] as FastColoredTextBox);
                if (t != null)
                {
                    for (var i = 0; i < t.LinesCount; i++)
                        if (t[i].LastVisit > _lastNavigatedDateTime && t[i].LastVisit < min)
                        {
                            min = t[i].LastVisit;
                            iLine = i;
                            tb = t;
                        }
                }
            }
            if (iLine >= 0)
            {
                if (tb != null)
                {
                    tsFiles.SelectedItem = (tb.Parent as FATabStripItem);
                    tb.Navigate(iLine);
                    _lastNavigatedDateTime = tb[iLine].LastVisit;
                    //Console.WriteLine("Forward: " + _lastNavigatedDateTime);
                    tb.Focus();
                    tb.Invalidate();
                }
            }
        }

        /// <summary>
        /// This item appears when any part of snippet text is typed
        /// </summary>
        class DeclarationSnippet : SnippetAutocompleteItem
        {
            public DeclarationSnippet(string snippet)
            : base(snippet)
            {
            }
            public override CompareResult Compare(string fragmentText)
            {
                var pattern = Regex.Escape(fragmentText);
                if (Regex.IsMatch(Text, "\\b" + pattern, RegexOptions.IgnoreCase))
                    return CompareResult.Visible;
                return CompareResult.Hidden;
            }
        }

        /// <summary>
        /// Divides numbers and words: "123AND456" -> "123 AND 456"
        /// Or "i=2" -> "i = 2"
        /// </summary>
        class InsertSpaceSnippet : AutocompleteItem
        {
            readonly string _pattern;

            public InsertSpaceSnippet(string pattern)
                : base("")
            {
                _pattern = pattern;
            }

            public InsertSpaceSnippet()
                : this(@"^(\d+)([a-zA-Z_]+)(\d*)$")
            {
            }

            public override CompareResult Compare(string fragmentText)
            {
                if (Regex.IsMatch(fragmentText, _pattern))
                {
                    Text = InsertSpaces(fragmentText);
                    if (Text != fragmentText)
                        return CompareResult.Visible;
                }
                return CompareResult.Hidden;
            }

            private string InsertSpaces(string fragment)
            {
                var m = Regex.Match(fragment, _pattern);
                if (m.Groups[1].Value == "" && m.Groups[3].Value == "")
                    return fragment;
                return (m.Groups[1].Value + " " + m.Groups[2].Value + " " + m.Groups[3].Value).Trim();
            }

            public override string ToolTipTitle
            {
            get
            {
            return Text;
            }
            }
        }

        /// <summary>
        /// Inerts line break after '}'
        /// </summary>
        class InsertEnterSnippet : AutocompleteItem
        {
            Place _enterPlace = Place.Empty;

            public InsertEnterSnippet()
                : base("[Line break]")
            {
            }

            public override CompareResult Compare(string fragmentText)
            {
                var r = Parent.Fragment.Clone();
                while (r.Start.iChar > 0)
                {
                    if (r.CharBeforeStart == '}')
                    {
                        _enterPlace = r.Start;
                        return CompareResult.Visible;
                    }

                    r.GoLeftThroughFolded();
                }

                return CompareResult.Hidden;
            }

            public override string GetTextForReplace()
            {
                //extend range
                Range r = Parent.Fragment;
                r.Start = _enterPlace;
                r.End = r.End;
                //insert line break
                return Environment.NewLine + r.Text;
            }

            public override void OnSelected(AutocompleteMenu popupMenu, SelectedEventArgs e)
            {
                base.OnSelected(popupMenu, e);
                if (Parent.Fragment.tb.AutoIndent)
                    Parent.Fragment.tb.DoAutoIndent();
            }

            public override string ToolTipTitle
            {
                get { return "Insert line break after '}'"; }
            }
        }

        private void autoIndentSelectedTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.DoAutoIndent();
        }

        private void btInvisibleChars_Click(object sender, EventArgs e)
        {
            if (sender == btInvisibleChars)
            {
                invisibleCharsToolStripMenuItem.Checked = btInvisibleChars.Checked;
            } else if (sender == invisibleCharsToolStripMenuItem)
            {
                btInvisibleChars.Checked = invisibleCharsToolStripMenuItem.Checked;
            }

            foreach (FATabStripItem tab in tsFiles.Items)
            {
                var fastColoredTextBox = tab.Controls[0] as FastColoredTextBox;
                if (fastColoredTextBox != null)
                    HighlightInvisibleChars(fastColoredTextBox.Range);
            }

            if (CurrentTb != null)
            {
                CurrentTb.Invalidate();
            }
        }

        private void btHighlightCurrentLine_Click(object sender, EventArgs e)
        {
            if (sender == btHighlightCurrentLine)
            {
                highlightCurrentLineToolStripMenuItem.Checked = btHighlightCurrentLine.Checked;
            }
            else if (sender == highlightCurrentLineToolStripMenuItem)
            {
                btHighlightCurrentLine.Checked = highlightCurrentLineToolStripMenuItem.Checked;
            }

            foreach (FATabStripItem tab in tsFiles.Items)
            {
                var fastColoredTextBox = tab.Controls[0] as FastColoredTextBox;
                if (fastColoredTextBox != null)
                    fastColoredTextBox.CurrentLineColor = btHighlightCurrentLine.Checked ? _currentLineColor : Color.Transparent;
            }
            if (CurrentTb != null) CurrentTb.Invalidate();
        }

        private void commentSelectedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.InsertLinePrefix("//");
        }

        private void uncommentSelectedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CurrentTb.RemoveLinePrefix("//");
        }

        private void cloneLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //expand selection
            CurrentTb.Selection.Expand();
            //get text of selected lines
            string text = Environment.NewLine + CurrentTb.Selection.Text;
            //move caret to end of selected lines
            CurrentTb.Selection.Start = CurrentTb.Selection.End;
            //insert text
            CurrentTb.InsertText(text);
        }

        private void cloneLinesAndCommentToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //start autoUndo block
            CurrentTb.BeginAutoUndo();
            //expand selection
            CurrentTb.Selection.Expand();
            //get text of selected lines
            string text = Environment.NewLine + CurrentTb.Selection.Text;
            //comment lines
            CurrentTb.InsertLinePrefix("//");
            //move caret to end of selected lines
            CurrentTb.Selection.Start = CurrentTb.Selection.End;
            //insert text
            CurrentTb.InsertText(text);
            //end of autoUndo block
            CurrentTb.EndAutoUndo();
        }

        private void bookmarkPlusButton_Click(object sender, EventArgs e)
        {
            if (CurrentTb != null) CurrentTb.BookmarkLine(CurrentTb.Selection.Start.iLine);
        }

        private void bookmarkMinusButton_Click(object sender, EventArgs e)
        {
            if (CurrentTb != null) CurrentTb.UnbookmarkLine(CurrentTb.Selection.Start.iLine);
        }

        private void gotoButton_DropDownOpening(object sender, EventArgs e)
        {
            gotoButton.DropDownItems.Clear();
            foreach (Control tab in tsFiles.Items)
            {
                FastColoredTextBox tb = tab.Controls[0] as FastColoredTextBox;
                if (tb != null)
                    foreach (var bookmark in tb.Bookmarks)
                    {
                        var item = gotoButton.DropDownItems.Add(bookmark.Name + " [" + Path.GetFileNameWithoutExtension(tab.Tag as String) + "]");
                        item.Tag = bookmark;
                        item.Click += (o, a) =>
                        {
                            var toolStripItem = o as ToolStripItem;
                            if (toolStripItem != null)
                                {
                                    var b = (Bookmark)toolStripItem.Tag;
                                    try
                                    {
                                        CurrentTb = b.TB;
                                    }
                                    catch (Exception ex)
                                    {
                                        MessageBox.Show(ex.Message);
                                        return;
                                    }
                                    b.DoVisible();
                                }
                        };
                    }
            }
        }

        private void btShowFoldingLines_Click(object sender, EventArgs e)
        {
            if (sender == btShowFoldingLines)
            {
                showFoldingLineToolStripMenuItem.Checked = btShowFoldingLines.Checked;
            }
            else if (sender == showFoldingLineToolStripMenuItem)
            {
                btShowFoldingLines.Checked = showFoldingLineToolStripMenuItem.Checked;
            }

            foreach (FATabStripItem tab in tsFiles.Items)
            {
                var fastColoredTextBox = tab.Controls[0] as FastColoredTextBox;
                if (fastColoredTextBox != null)
                    fastColoredTextBox.ShowFoldingLines = btShowFoldingLines.Checked;
            }
            if (CurrentTb != null) CurrentTb.Invalidate();
        }

        private void Zoom_click(object sender, EventArgs e)
        {
            if (CurrentTb != null)
            {
                var toolStripItem = sender as ToolStripItem;
                if (toolStripItem != null)
                    CurrentTb.Zoom = int.Parse(toolStripItem.Tag.ToString());
            }
        }

        private void toolStripRescanButton_Click(object sender, EventArgs e)
        {
            RefreshSerialPort();
        }

        private void toolStripDownloadButton_Click(object sender, EventArgs e)
        {
            if (tsFiles.SelectedItem == null)
            {
                MessageBox.Show(Resources.no_file_selected);
                return;
            }

            if (tsFiles.SelectedItem.Tag == null)
            {
                MessageBox.Show(Resources.save_current_file_first);
                return;
            }

            var filename = Path.GetFileName(tsFiles.SelectedItem.Tag as string);

            DoSerialPortAction(
                () => ExecuteWaitAndRead(string.Format("file.remove(\"{0}\")", Utilities.Escape(filename)), _ =>
                    ExecuteWaitAndRead(string.Format("file.open(\"{0}\", \"w+\")", Utilities.Escape(filename)), __ =>
                    {
                        if (
                            CurrentTb.Text.Split('\n')
                                .Any(
                                    line =>
                                        !SerialPort.GetInstance()
                                            .ExecuteAndWait(string.Format("file.writeline(\"{0}\")",
                                                Utilities.Escape(line)))))
                        {
                            SerialPort.GetInstance().ExecuteAndWait("file.close()");
                            MessageBox.Show(Resources.download_to_device_failed);
                        }
                        else
                        {
                            MessageBox.Show(!SerialPort.GetInstance().ExecuteAndWait("file.close()")
                                ? Resources.download_to_device_failed
                                : Resources.download_to_device_succeeded);
                        }
                    })), () => { });
        }

        private void RefreshSerialPort()
        {
            if (toolStripComboBoxSerialPort.ComboBox != null)
                toolStripComboBoxSerialPort.ComboBox.DataSource = SerialPort.GetPortNames();
        }

        private void toolStripRunButton_Click(object sender, EventArgs e)
        {
            var index = toolStripComboBoxSerialPort.SelectedIndex;
            if (index < 0)
            {
                MessageBox.Show(Resources.no_serial_port_selected);
                return;
            }

            if (tsFiles.SelectedItem == null)
            {
                MessageBox.Show(Resources.no_file_selected);
                return;
            }

            if (tsFiles.SelectedItem.Tag == null)
            {
                MessageBox.Show(Resources.save_current_file_first);
                return;
            }

            var filename = Path.GetFileName(tsFiles.SelectedItem.Tag as string);

            DoSerialPortAction(
                () => ExecuteWaitAndRead(string.Format("dofile(\"{0}\")", Utilities.Escape(filename)), _ =>
                    {
                        MessageBox.Show(Resources.execute_succeeded);
                    }), () => { });
        }

        private void toolStripCloseButton_Click(object sender, EventArgs e)
        {
            SerialPort.GetInstance().Close();
        }

        private void DoSerialPortAction(Action callback)
        {
            DoSerialPortAction(callback, () => { });
        }

        private void DoSerialPortAction(Action callback, Action cleanup)
        {
            var index = toolStripComboBoxSerialPort.SelectedIndex;
            if (index < 0)
            {
                MessageBox.Show(Resources.no_serial_port_selected);
                cleanup();
                return;
            }

            if (toolStripComboBoxSerialPort.ComboBox != null)
            {
                var ports = toolStripComboBoxSerialPort.ComboBox.DataSource as string[];
                if (ports == null ||
                    (!SerialPort.GetInstance().CurrentSp.IsOpen && !SerialPort.GetInstance().Open(ports[index])))
                {
                    MessageBox.Show(Resources.cannot_connect_to_device);
                    cleanup();
                    return;
                }
            }

            Task task = new Task(() =>
            {
                lock(SerialPort.GetInstance().Lock)
                {
                    SerialPort.GetInstance().FireIsWorkingChanged(true);

                    try
                    {
                        callback();
                    }
                    catch (IgnoreMeException)
                    {
                        // Ignore me.
                    }
                    catch (Exception exception)
                    {
                        MessageBox.Show(string.Format("Operation failed: "+ exception));
                    }
                    
                }
                SerialPort.GetInstance().FireIsWorkingChanged(false);
            });

            task.ContinueWith(_ => cleanup() , TaskScheduler.FromCurrentSynchronizationContext());
            task.Start();
        }

        private static void ExecuteWaitAndRead(string command, Action<string> callback)
        {
            var line = SerialPort.GetInstance().ExecuteWaitAndRead(command);
            if (line.Length == 2 /* \r and \n */ || line.Equals("stdin:1: open a file first\r\n"))
            {
                MessageBox.Show(Resources.operation_failed);
                throw new IgnoreMeException();
            }
            callback(line);
        }

        private static void ExecuteWaitAndRead(string command)
        {
            ExecuteWaitAndRead(command, _ => { });
        }

        private void toolStripUploadButton_Click(object sender, EventArgs e)
        {
            var label = new Label
            {
                Left = 16,
                Top = 20,
                Width = 240,
                Text = Resources.please_choose_file_to_upload
            };

            var files = new TextBox
            {
                Left = 16,
                Width = 240,
                AutoCompleteSource = AutoCompleteSource.CustomSource,
                AutoCompleteMode = AutoCompleteMode.Suggest,
                AutoCompleteCustomSource = new AutoCompleteStringCollection()
            };

            var upload = new Button
            {
                Left = 75,
                Width = 80,
                Top = 80,
                TabIndex = 1,
                TabStop = true,
                Text = Resources.upload_text
            };
            var prompt = new Form
            {
                Width = 300,
                Height = 160,
                MinimumSize = new Size
                {
                    Height = 160,
                    Width = 280
                },
                MaximumSize = new Size
                {
                    Height = 160,
                    Width = 280
                },
                Text = Resources.upload_text,
                Controls =
                {
                    label,
                    files,
                    upload
                },
                AcceptButton = upload,
                Icon = Resources.nodemcu
            };

            var result = "";

            DoSerialPortAction(
                () => ExecuteWaitAndRead("for k, v in pairs(file.list()) do", _ =>
                    ExecuteWaitAndRead("print(k)", __ => ExecuteWaitAndRead("end", str =>
                    {
                        result = str;
                    }))), () => { files.AutoCompleteCustomSource.Clear(); files.AutoCompleteCustomSource.AddRange(result.Split('\n')); });

            upload.Click += (o, args) =>
            {
                prompt.Close();

                var s = files.Text;

                if (s == null)
                {
                    MessageBox.Show(Resources.no_file_selected);
                    return;
                }

                string res = "";

                DoSerialPortAction(
                () => ExecuteWaitAndRead(string.Format("file.open(\"{0}\", \"r\")", Utilities.Escape(s)), _ =>
                    {
                        var builder = new StringBuilder();
                        while (true)
                        {
                            try
                            {
                                ExecuteWaitAndRead("print(file.readline())", line =>
                                {
                                    builder.Append(line);
                                });
                            }
                            catch (IgnoreMeException)
                            {
                                // ignore
                                break;
                            }
                        }
                        res = builder.ToString();
                        SerialPort.GetInstance()
                            .ExecuteAndWait("file.close()");

                    }), () =>
                    {
                        CreateTab(null);
                        CurrentTb.InsertText(res);
                    });
            };

            prompt.ShowDialog();
        }

        private void textBoxCommand_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar != (char)Keys.Return) return;
            var command = textBoxCommand.Text;
            textBoxCommand.Text = "";

            DoSerialPortAction(() => ExecuteWaitAndRead(command));
        }

        [Serializable]
        private class IgnoreMeException : ApplicationException
        {

        };

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch(e.KeyData) {
                case (Keys.Control | Keys.N):
                    newToolStripMenuItem_Click(null, null);
                    break;
                case (Keys.Control | Keys.O):
                    openToolStripMenuItem_Click(null, null);
                    break;
                case (Keys.Control | Keys.S):
                    saveToolStripMenuItem_Click(null, null);
                    break;
                case (Keys.Alt | Keys.F4):
                    Close();
                    break;
            }
        }

        private void PowerfulLuaEditor_Load(object sender, EventArgs e)
        {

        }

        private void оПрограммеToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Среда разработки для устройств основанных на прошивке NODEMCU, оригинальные исходники лежат на GitHub\nhttps://github.com/nodemcu/nodemcu-studio-csharp\n Исходники этой версии лежат на GitHub");

        }

        private void rescanToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
    }

    public class InvisibleCharsRenderer : Style
    {
        readonly Pen _pen;

        public InvisibleCharsRenderer(Pen pen)
        {
            _pen = pen;
        }

        public override void Draw(Graphics gr, Point position, Range range)
        {
            var tb = range.tb;
            using(Brush brush = new SolidBrush(_pen.Color))
            foreach (var place in range)
            {
                switch (tb[place].c)
                {
                    case ' ':
                        var point = tb.PlaceToPoint(place);
                        point.Offset(tb.CharWidth / 2, tb.CharHeight / 2);
                        gr.DrawLine(_pen, point.X, point.Y, point.X + 1, point.Y);
                        break;
                }

                if (tb[place.iLine].Count - 1 == place.iChar)
                {
                    var point = tb.PlaceToPoint(place);
                    point.Offset(tb.CharWidth, 0);
                    gr.DrawString("¶", tb.Font, brush, point);
                }
            }
        }
    }

    public class TbInfo
    {
        public AutocompleteMenu PopupMenu;
    }
}
