using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using CryBar;
using CryBar.Classes;
using CryBar.TMM;
using CryBarEditor.Classes;

using Material.Icons;
using Material.Icons.Avalonia;

using SixLabors.ImageSharp.PixelFormats;

using Avalonia.Threading;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using IOPath = System.IO.Path;

namespace CryBarEditor;

public partial class DependencyGraphWindow : SimpleWindow
{
    // Singleton
    static DependencyGraphWindow? _instance;

    // Data
    DependencyGroupItem? _group;
    FileIndex? _fileIndex;
    MainWindow? _mainWindow;

    // Pan/Zoom state
    bool _isPanning;
    Point _panStart;
    double _zoomLevel = 1.0;
    const double ZoomMin = 0.1, ZoomMax = 3.0;

    // Node drag state
    bool _isDraggingNode;
    Border? _draggedNode;
    Point _dragStart;
    double _dragNodeStartX, _dragNodeStartY;

    // Multi-select state
    readonly HashSet<Border> _selectedNodes = new();
    readonly Dictionary<Border, (double X, double Y)> _multiDragStartPositions = new();
    bool _isRectSelecting;
    Point _rectSelectStart;
    Border? _selectionRect;

    // Graph state
    readonly List<Border> _nodeElements = [];
    readonly HashSet<Border> _nodeElementsSet = new();
    readonly List<Line> _edgeElements = [];
    readonly Dictionary<Border, List<Line>> _nodeEdges = new();
    readonly Dictionary<Border, (double X, double Y)> _nodePositions = new();
    readonly HashSet<string> _visitedPaths = new();
    // Tracks all nodes+edges spawned by an expansion (for collapse)
    readonly Dictionary<Border, List<Control>> _expansionChildren = new();

    // Animation state
    bool _isAnimating;
    DispatcherTimer? _animTimer;
    readonly Dictionary<Border, (double X, double Y)> _animStartPositions = new();
    readonly Dictionary<Border, (double X, double Y)> _animTargetPositions = new();
    readonly Dictionary<Border, Size> _animNodeSizes = new();
    double _animProgress;
    const double AnimDurationMs = 300;
    const double AnimIntervalMs = 16; // ~60fps
    Action? _animCompleteCallback;

    // Bitmaps created for previews (disposed on graph rebuild/close)
    readonly List<Bitmap> _previewBitmaps = [];

    // FMOD playback state
    CancellationTokenSource? _playbackCts;
    FMODBank? _playbackBank;

    // Transform resolved after InitializeComponent
    MatrixTransform _graphTransform = null!;

    // Colors
    static readonly Color FilePathColor = Color.Parse("#d9d9d9");
    static readonly Color StringKeyColor = Color.Parse("#c4a96a");
    static readonly Color SoundsetColor = Color.Parse("#6f96bf");
    static readonly Color CenterBg = Color.Parse("#2b3c57");
    static readonly Color CenterBorder = Color.Parse("#6f96bf");

    static readonly Color TmmColor = Color.Parse("#8bc34a");
    static readonly Color TmmDataColor = Color.Parse("#7cb342");
    static readonly Color TextureColor = Color.Parse("#ce93d8");
    static readonly Color MaterialColor = Color.Parse("#ffb74d");
    static readonly Color FbxImportColor = Color.Parse("#90a4ae");
    static readonly Color XmlColor = Color.Parse("#d9d9d9");
    static readonly Color GenericColor = Color.Parse("#808080");

    const double NodeWidth = 220;

    int _baseRefCount;
    int _expandedRefCount;

    public string WindowTitle { get; private set; } = "Dependency Graph";
    public string StatusText { get; private set; } = "";

    void UpdateStatusText()
    {
        StatusText = _expandedRefCount > 0
            ? $"{_baseRefCount} references (+{_expandedRefCount} expanded)"
            : $"{_baseRefCount} references";
        OnPropertyChanged(nameof(StatusText));
    }

    CancellationTokenSource? _thumbnailCts;

    public DependencyGraphWindow()
    {
        DataContext = this;
        InitializeComponent();

        _graphTransform = (MatrixTransform)GraphCanvas.RenderTransform!;

        CanvasHost.AddHandler(PointerPressedEvent, CanvasHost_PointerPressed, RoutingStrategies.Tunnel);
        CanvasHost.AddHandler(PointerMovedEvent, CanvasHost_PointerMoved, RoutingStrategies.Tunnel);
        CanvasHost.AddHandler(PointerReleasedEvent, CanvasHost_PointerReleased, RoutingStrategies.Tunnel);
        CanvasHost.AddHandler(PointerWheelChangedEvent, CanvasHost_PointerWheelChanged, RoutingStrategies.Tunnel);
    }

    public static void ShowForGroup(DependencyGroupItem group, FileIndex? fileIndex, MainWindow owner, Window ownerWindow)
    {
        if (_instance != null)
        {
            _instance._group = group;
            _instance._fileIndex = fileIndex;
            _instance._mainWindow = owner;
            _instance.BuildGraph();
            _instance.Focus();
            return;
        }

        _instance = new DependencyGraphWindow
        {
            _group = group,
            _fileIndex = fileIndex,
            _mainWindow = owner
        };
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.BuildGraph();
    }

    void BuildGraph()
    {
        if (_group == null) return;

        SnapAnimationToEnd();

        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();

        DisposePlaybackBank();
        DisposeBitmaps();

        GraphCanvas.Children.Clear();
        _nodeElements.Clear();
        _nodeElementsSet.Clear();
        _edgeElements.Clear();
        _nodeEdges.Clear();
        _nodePositions.Clear();
        _visitedPaths.Clear();
        _expansionChildren.Clear();
        _selectedNodes.Clear();
        _multiDragStartPositions.Clear();

        WindowTitle = $"Dependency Graph \u2014 {_group.DisplayName}";
        OnPropertyChanged(nameof(WindowTitle));
        Title = WindowTitle;

        var refs = _group.References;
        _baseRefCount = refs.Count;
        _expandedRefCount = 0;
        UpdateStatusText();

        var centerX = 0.0;
        var centerY = 0.0;

        var centerNode = CreateCenterNode(_group.DisplayName, _group.EntityTypeLabel);
        PlaceNode(centerNode, centerX, centerY);

        if (refs.Count == 0) { ResetView(); return; }

        // Pre-compute visible matches once per reference to avoid duplicate work
        var visibleMatchesMap = new Dictionary<DependencyReference, List<FileIndexEntry>>(refs.Count);
        bool hasSubNodes = false;
        foreach (var r in refs)
        {
            var matches = GetVisibleMatches(r);
            visibleMatchesMap[r] = matches;
            if (matches.Count > 0) hasSubNodes = true;
        }

        var grouped = refs.GroupBy(r => r.Type).OrderBy(g => g.Key).ToList();

        const int RadialThreshold = 12;

        if (refs.Count <= RadialThreshold)
        {
            LayoutRadial(centerNode, centerX, centerY, grouped, visibleMatchesMap, hasSubNodes);
        }
        else
        {
            LayoutColumnar(centerNode, centerX, centerY, grouped, visibleMatchesMap);
        }

        ResetView();
    }

    /// <summary>
    /// Radial layout for small graphs (≤12 refs). Nodes arranged in a circle around center.
    /// Uses measured node widths for spacing to prevent overlaps.
    /// </summary>
    void LayoutRadial(Border centerNode, double centerX, double centerY,
        List<IGrouping<DependencyRefType, DependencyReference>> grouped,
        Dictionary<DependencyReference, List<FileIndexEntry>> visibleMatchesMap,
        bool hasSubNodes)
    {
        var refs = grouped.SelectMany(g => g).ToList();

        // Pre-create all nodes and measure their actual widths
        var nodeInfos = new List<(DependencyReference Ref, Border Node, Color Color, double Width)>();
        foreach (var r in refs)
        {
            var refColor = GetRefTypeColor(r.Type);
            var refNode = CreateReferenceNode(r, refColor);
            nodeInfos.Add((r, refNode, refColor, MeasureNode(refNode).Width));
        }

        // Compute radius from actual node widths + padding for sub-nodes
        double subNodePadding = hasSubNodes ? 80 : 20;
        double totalArcLength = nodeInfos.Sum(n => n.Width + subNodePadding);
        double radius = Math.Max(250, totalArcLength / (2 * Math.PI));

        double totalAngle = 2 * Math.PI;
        double currentAngle = -Math.PI / 2;

        // Re-group for arc allocation per type
        int refIdx = 0;
        foreach (var typeGroup in grouped)
        {
            var groupRefs = typeGroup.ToList();
            double arcSpan = totalAngle * groupRefs.Count / refs.Count;

            for (int i = 0; i < groupRefs.Count; i++)
            {
                var info = nodeInfos[refIdx++];
                double angle = currentAngle + (i + 0.5) * arcSpan / groupRefs.Count;
                double nx = centerX + radius * Math.Cos(angle);
                double ny = centerY + radius * Math.Sin(angle);

                PlaceNode(info.Node, nx, ny);

                var edge = CreateEdge(centerX, centerY, nx, ny, info.Color, 1.5);
                ConnectEdge(centerNode, edge);
                ConnectEdge(info.Node, edge);

                var visibleMatches = visibleMatchesMap[info.Ref];
                if (visibleMatches.Count > 0)
                    LayoutMatchSubNodes(info.Node, visibleMatches, nx, ny, angle);
            }

            currentAngle += arcSpan;
        }
    }

    /// <summary>
    /// Columnar grid layout for large graphs (>12 refs). Each type group becomes
    /// a column radiating outward from center. Nodes stack vertically within columns
    /// with spacing based on actual measured dimensions.
    /// </summary>
    void LayoutColumnar(Border centerNode, double centerX, double centerY,
        List<IGrouping<DependencyRefType, DependencyReference>> grouped,
        Dictionary<DependencyReference, List<FileIndexEntry>> visibleMatchesMap)
    {
        int groupCount = grouped.Count;
        double angleStep = 2 * Math.PI / Math.Max(groupCount, 1);
        double startAngle = -Math.PI / 2;

        for (int g = 0; g < groupCount; g++)
        {
            var typeGroup = grouped[g];
            var groupRefs = typeGroup.ToList();
            double groupAngle = startAngle + g * angleStep;

            // Direction vector for this column
            double dirX = Math.Cos(groupAngle);
            double dirY = Math.Sin(groupAngle);
            // Perpendicular direction for stacking
            double perpX = -dirY;
            double perpY = dirX;

            // Pre-create and measure all nodes in this column
            var columnNodes = new List<(DependencyReference Ref, Border Node, Color Color, double Width, double Height)>();
            foreach (var r in groupRefs)
            {
                var refColor = GetRefTypeColor(r.Type);
                var refNode = CreateReferenceNode(r, refColor);
                var size = MeasureNode(refNode);
                columnNodes.Add((r, refNode, refColor, size.Width, size.Height));
            }

            // Compute column dimensions
            double maxWidth = columnNodes.Max(n => n.Width);
            double verticalSpacing = 8;
            double totalHeight = columnNodes.Sum(n => n.Height + verticalSpacing) - verticalSpacing;

            // Column distance from center — close enough to be readable
            // Account for sub-nodes by using more distance when matches exist
            bool columnHasSubNodes = groupRefs.Any(r => visibleMatchesMap[r].Count > 0);
            double colDistance = 200 + (columnHasSubNodes ? maxWidth * 0.5 : 0);

            // Column center point
            double colCenterX = centerX + dirX * colDistance;
            double colCenterY = centerY + dirY * colDistance;

            // Stack nodes along the perpendicular direction, centered on column center
            double stackOffset = -totalHeight / 2;

            for (int i = 0; i < columnNodes.Count; i++)
            {
                var info = columnNodes[i];
                double perpOffset = stackOffset + info.Height / 2;
                double nx = colCenterX + perpX * perpOffset;
                double ny = colCenterY + perpY * perpOffset;

                PlaceNode(info.Node, nx, ny);

                var edge = CreateEdge(centerX, centerY, nx, ny, info.Color, 1.5);
                ConnectEdge(centerNode, edge);
                ConnectEdge(info.Node, edge);

                // Sub-nodes: place along the same outward direction from this node
                var visibleMatches = visibleMatchesMap[info.Ref];
                if (visibleMatches.Count > 0)
                    LayoutMatchSubNodes(info.Node, visibleMatches, nx, ny, groupAngle);

                stackOffset += info.Height + verticalSpacing;
            }
        }
    }

    /// <summary>
    /// Returns resolved matches that should be shown as separate sub-nodes.
    /// - StringKey: never show string_table.txt sub-nodes
    /// - Single resolved match: merged into parent node, not shown as sub-node
    /// - Multiple resolved matches: all shown as sub-nodes
    /// </summary>
    static List<FileIndexEntry> GetVisibleMatches(DependencyReference r)
    {
        if (r.Type == DependencyRefType.StringKey)
            return []; // string_table.txt links are not useful

        if (r.Resolved.Count <= 1)
            return []; // single match merged into parent node

        return r.Resolved;
    }

    void LayoutMatchSubNodes(Border parentNode, List<FileIndexEntry> matches, double parentX, double parentY, double parentAngle, List<Control>? trackingList = null)
    {
        // Pre-create and measure all match nodes
        var matchNodes = new List<(FileIndexEntry Match, Border Node, Color Color, MaterialIconKind Icon, double Width)>();
        foreach (var match in matches)
        {
            var (matchColor, matchIcon) = GetMatchFileStyle(match.FileName);
            var matchNode = CreateMatchNode(match, matchColor, matchIcon);
            matchNodes.Add((match, matchNode, matchColor, matchIcon, MeasureNode(matchNode).Width));
        }

        // Use measured widths for spacing
        double maxWidth = matchNodes.Max(n => n.Width);
        double subCircumference = matches.Count * (maxWidth + 20);
        double subRadius = Math.Max(100, subCircumference / Math.PI); // half circle
        double subArcSpan = Math.Min(Math.PI / 2, matches.Count * 0.35);

        for (int j = 0; j < matchNodes.Count; j++)
        {
            var info = matchNodes[j];
            double subAngle = matches.Count == 1
                ? parentAngle
                : parentAngle - subArcSpan / 2 + j * subArcSpan / Math.Max(1, matches.Count - 1);
            double sx = parentX + subRadius * Math.Cos(subAngle);
            double sy = parentY + subRadius * Math.Sin(subAngle);

            PlaceNode(info.Node, sx, sy);
            trackingList?.Add(info.Node);

            var subEdge = CreateEdge(parentX, parentY, sx, sy, info.Color, 1.0);
            ConnectEdge(parentNode, subEdge);
            ConnectEdge(info.Node, subEdge);
            trackingList?.Add(subEdge);
        }
    }

    #region Node Creation

    Border CreateCenterNode(string name, string entityType)
    {
        var stack = new StackPanel { Spacing = 2 };

        if (!string.IsNullOrEmpty(entityType))
        {
            stack.Children.Add(new TextBlock
            {
                Text = entityType,
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                FontSize = 10,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });

        var border = new Border
        {
            Background = new SolidColorBrush(CenterBg),
            BorderBrush = new SolidColorBrush(CenterBorder),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = stack,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        SetupNodeDrag(border);
        return border;
    }

    Border CreateReferenceNode(DependencyReference reference, Color color)
    {
        var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };

        // Type icon
        stack.Children.Add(new MaterialIcon
        {
            Kind = GetRefTypeIcon(reference.Type),
            Width = 14, Height = 14,
            Foreground = new SolidColorBrush(color)
        });

        var contentStack = new StackPanel { Spacing = 1 };

        // Value text — trim from left to keep filename visible
        var displayText = reference.RawValue;
        if (displayText.Length > 50)
            displayText = "..." + displayText[^47..];

        contentStack.Children.Add(new TextBlock
        {
            Text = displayText,
            Foreground = new SolidColorBrush(color),
            FontSize = 11
        });

        // Single resolved match: show resolved filename inline (merged node)
        if (reference.Resolved.Count == 1 && reference.Type != DependencyRefType.StringKey)
        {
            var resolved = reference.Resolved[0];
            var resolvedName = IOPath.GetFileName(resolved.FileName);
            // Only show if different from raw value
            if (!resolvedName.Equals(IOPath.GetFileName(reference.RawValue), StringComparison.OrdinalIgnoreCase))
            {
                var (matchColor, matchIcon) = GetMatchFileStyle(resolvedName);
                var resolvedLine = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3 };
                resolvedLine.Children.Add(new MaterialIcon
                {
                    Kind = matchIcon, Width = 10, Height = 10,
                    Foreground = new SolidColorBrush(matchColor)
                });
                resolvedLine.Children.Add(new TextBlock
                {
                    Text = resolvedName,
                    Foreground = new SolidColorBrush(Color.Parse("#808080")),
                    FontSize = 9,
                    MaxWidth = 180,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                contentStack.Children.Add(resolvedLine);
            }

            // Image/TMM preview is lazy-loaded via eye button click only
        }

        // StringKey: show translated text
        if (reference.Type == DependencyRefType.StringKey)
        {
            var translationBlock = new TextBlock
            {
                Text = "...",
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                FontSize = 9,
                FontStyle = FontStyle.Italic,
                MaxWidth = 200,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            contentStack.Children.Add(translationBlock);
            _ = LoadStringTranslationAsync(translationBlock, reference.RawValue);
        }

        // Expandable file references: any text-based file can be recursively loaded
        if (reference.Type == DependencyRefType.FilePath && reference.Resolved.Count > 0)
        {
            var resolvedName = reference.Resolved[0].FileName;
            if (CanExpandRecursively(resolvedName))
            {
                var loadBtn = new Button
                {
                    Content = "\u25B6 Load",
                    FontSize = 10,
                    Padding = new Thickness(4, 1),
                    Background = new SolidColorBrush(Color.Parse("#2a2a2a")),
                    Foreground = new SolidColorBrush(color),
                    BorderThickness = new Thickness(0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Margin = new Thickness(0, 2, 0, 0),
                    Tag = reference
                };
                loadBtn.Click += RecursiveLoad_Click;
                contentStack.Children.Add(loadBtn);
            }
        }

        // Soundset: add play button
        if (reference.Type == DependencyRefType.SoundsetName)
        {
            var playBtn = new Button
            {
                Padding = new Thickness(2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(4, 0, 0, 0),
                Tag = reference,
                Content = new MaterialIcon
                {
                    Kind = MaterialIconKind.Play,
                    Width = 12, Height = 12,
                    Foreground = new SolidColorBrush(SoundsetColor)
                }
            };
            playBtn.Click += PlaySoundset_Click;
            stack.Children.Add(playBtn);
        }

        // Preview button for image/TMM references (single resolved only — multi has sub-nodes)
        if (reference.Resolved.Count == 1)
        {
            var resolved = reference.Resolved[0];
            var previewBtn = CreatePreviewButton(resolved);
            if (previewBtn != null)
                stack.Children.Add(previewBtn);
        }

        stack.Children.Insert(1, contentStack);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141414")),
            BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
            BorderThickness = new Thickness(2, 1, 1, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Child = stack,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = reference
        };

        border.BorderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.Parse("#333333"), 0.15)
            }
        };

        ToolTip.SetTip(border, reference.RawValue);
        border.DoubleTapped += Node_DoubleTapped;
        SetupNodeDrag(border);

        return border;
    }

    Border CreateMatchNode(FileIndexEntry match, Color color, MaterialIconKind icon)
    {
        var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };

        stack.Children.Add(new MaterialIcon
        {
            Kind = icon,
            Width = 12, Height = 12,
            Foreground = new SolidColorBrush(color)
        });

        var fileName = IOPath.GetFileName(match.FileName);
        if (fileName.Length > 35)
            fileName = "..." + fileName[^32..];

        stack.Children.Add(new TextBlock
        {
            Text = fileName,
            Foreground = new SolidColorBrush(color),
            FontSize = 10,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Preview button for image/TMM files
        var matchPreviewBtn = CreatePreviewButton(match);
        if (matchPreviewBtn != null)
            stack.Children.Add(matchPreviewBtn);

        // Expand button for files that can be parsed for further dependencies
        if (CanExpandRecursively(match.FileName))
        {
            var expandBtn = new Button
            {
                Content = "\u25B6",
                FontSize = 9,
                Padding = new Thickness(2, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(color),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(2, 0, 0, 0),
                Tag = match
            };
            ToolTip.SetTip(expandBtn, "Expand dependencies");
            expandBtn.Click += MatchExpandCollapse_Click;
            stack.Children.Add(expandBtn);
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141414")),
            BorderBrush = new SolidColorBrush(color, 0.4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 3),
            Child = stack,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = match
        };

        ToolTip.SetTip(border, match.FullRelativePath);
        border.DoubleTapped += Node_DoubleTapped;
        SetupNodeDrag(border);

        return border;
    }

    Button? CreatePreviewButton(FileIndexEntry entry)
    {
        if (!IsDdtOrImage(entry.FileName) && !IsTmm(entry.FileName)) return null;

        var btn = new Button
        {
            Padding = new Thickness(1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(2, 0, 0, 0),
            Tag = entry,
            Content = new MaterialIcon
            {
                Kind = MaterialIconKind.Eye,
                Width = 12, Height = 12,
                Foreground = new SolidColorBrush(IsTmm(entry.FileName) ? TmmColor : TextureColor)
            }
        };
        ToolTip.SetTip(btn, "Preview in node");
        btn.Click += PreviewImage_Click;
        return btn;
    }

    #endregion

    #region Edges

    Line CreateEdge(double x1, double y1, double x2, double y2, Color color, double thickness)
    {
        var line = new Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Stroke = new SolidColorBrush(color, 0.4),
            StrokeThickness = thickness
        };
        GraphCanvas.Children.Add(line);
        _edgeElements.Add(line);
        return line;
    }

    void ConnectEdge(Border node, Line edge)
    {
        if (!_nodeEdges.TryGetValue(node, out var edges))
        {
            edges = [];
            _nodeEdges[node] = edges;
        }
        edges.Add(edge);
    }

    void UpdateEdgesForNode(Border node, double newX, double newY)
    {
        if (!_nodeEdges.TryGetValue(node, out var edges)) return;
        if (!_nodePositions.TryGetValue(node, out var oldPos)) return;

        foreach (var edge in edges)
        {
            if (Math.Abs(edge.StartPoint.X - oldPos.X) < 1 && Math.Abs(edge.StartPoint.Y - oldPos.Y) < 1)
                edge.StartPoint = new Point(newX, newY);
            else
                edge.EndPoint = new Point(newX, newY);
        }
    }

    #endregion

    #region Layout

    void PlaceNode(Border node, double x, double y)
    {
        // Use DesiredSize if already measured, otherwise measure now
        var size = node.DesiredSize.Width > 0 ? node.DesiredSize : MeasureNode(node);

        Canvas.SetLeft(node, x - size.Width / 2);
        Canvas.SetTop(node, y - size.Height / 2);
        GraphCanvas.Children.Add(node);
        _nodeElements.Add(node);
        _nodeElementsSet.Add(node);
        _nodePositions[node] = (x, y);
    }

    /// <summary>
    /// Measures a node and returns its desired size. Caches the result in DesiredSize.
    /// </summary>
    static Size MeasureNode(Border node)
    {
        node.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return node.DesiredSize;
    }

    /// <summary>
    /// Builds an AABB bounding box for a node at a given center position.
    /// </summary>
    static (double Left, double Top, double Right, double Bottom) GetNodeBox(Border node, double cx, double cy, double padding)
    {
        var size = node.DesiredSize;
        return (
            cx - size.Width / 2 - padding / 2,
            cy - size.Height / 2 - padding / 2,
            cx + size.Width / 2 + padding / 2,
            cy + size.Height / 2 + padding / 2
        );
    }

    /// <summary>
    /// Checks AABB overlap between two boxes.
    /// </summary>
    static bool BoxesOverlap(
        (double Left, double Top, double Right, double Bottom) a,
        (double Left, double Top, double Right, double Bottom) b)
    {
        return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    }

    /// <summary>
    /// Resolves overlaps among a set of nodes by iteratively pushing overlapping pairs apart.
    /// Both nodes in each overlapping pair are moved (each by half the required distance).
    /// Runs multiple passes to handle cascading overlaps.
    /// Returns target positions for all nodes that moved.
    /// </summary>
    Dictionary<Border, (double X, double Y)> ResolveInternalOverlaps(HashSet<Border> nodeSet, double padding = 20, int maxPasses = 4)
    {
        // Work with a mutable copy of current positions
        var positions = new Dictionary<Border, (double X, double Y)>();
        foreach (var node in nodeSet)
        {
            if (_nodePositions.TryGetValue(node, out var pos))
                positions[node] = pos;
        }

        var nodeList = positions.Keys.ToList();
        bool anyMoved = false;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool movedThisPass = false;

            for (int i = 0; i < nodeList.Count; i++)
            {
                var nodeA = nodeList[i];
                var posA = positions[nodeA];
                var boxA = GetNodeBox(nodeA, posA.X, posA.Y, padding);

                for (int j = i + 1; j < nodeList.Count; j++)
                {
                    var nodeB = nodeList[j];
                    var posB = positions[nodeB];
                    var boxB = GetNodeBox(nodeB, posB.X, posB.Y, padding);

                    if (!BoxesOverlap(boxA, boxB)) continue;

                    // Push apart: each node moves half the overlap distance
                    var dx = posB.X - posA.X;
                    var dy = posB.Y - posA.Y;
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist < 1) { dx = 1; dy = 0; dist = 1; }

                    var overlapX = Math.Min(boxA.Right - boxB.Left, boxB.Right - boxA.Left);
                    var overlapY = Math.Min(boxA.Bottom - boxB.Top, boxB.Bottom - boxA.Top);
                    var pushDist = (Math.Min(overlapX, overlapY) + padding) / 2;

                    var pushX = (dx / dist) * pushDist;
                    var pushY = (dy / dist) * pushDist;

                    positions[nodeA] = (posA.X - pushX, posA.Y - pushY);
                    positions[nodeB] = (posB.X + pushX, posB.Y + pushY);

                    // Refresh boxA for subsequent comparisons in this pass
                    posA = positions[nodeA];
                    boxA = GetNodeBox(nodeA, posA.X, posA.Y, padding);

                    movedThisPass = true;
                }
            }

            if (!movedThisPass) break;
            anyMoved = true;
        }

        if (!anyMoved) return new Dictionary<Border, (double X, double Y)>();

        // Return only nodes that actually moved
        var result = new Dictionary<Border, (double X, double Y)>();
        foreach (var (node, newPos) in positions)
        {
            if (!_nodePositions.TryGetValue(node, out var origPos)) continue;
            if (Math.Abs(newPos.X - origPos.X) > 0.5 || Math.Abs(newPos.Y - origPos.Y) > 0.5)
                result[node] = newPos;
        }
        return result;
    }

    /// <summary>
    /// Computes overlap-free positions for newly placed nodes by pushing existing nodes away.
    /// Returns a dictionary of nodes that need to move and their target positions.
    /// </summary>
    Dictionary<Border, (double X, double Y)> ComputeOverlapDisplacements(HashSet<Border> newNodes, double padding = 20)
    {
        // First: resolve overlaps among the new nodes themselves
        var displacements = ResolveInternalOverlaps(newNodes, padding);

        // Apply internal displacements immediately (so bounding boxes are accurate for external push)
        foreach (var (node, target) in displacements)
        {
            var size = node.DesiredSize;
            Canvas.SetLeft(node, target.X - size.Width / 2);
            Canvas.SetTop(node, target.Y - size.Height / 2);
            UpdateEdgesForNode(node, target.X, target.Y);
            _nodePositions[node] = target;
        }

        // Second: push existing nodes away from (now resolved) new nodes
        var boxes = new Dictionary<Border, (double Left, double Top, double Right, double Bottom)>();
        foreach (var node in _nodeElements)
        {
            if (!_nodePositions.TryGetValue(node, out var pos)) continue;
            boxes[node] = GetNodeBox(node, pos.X, pos.Y, padding);
        }

        foreach (var newNode in newNodes)
        {
            if (!boxes.TryGetValue(newNode, out var newBox)) continue;

            foreach (var existingNode in _nodeElements)
            {
                if (newNodes.Contains(existingNode)) continue;
                if (!boxes.TryGetValue(existingNode, out var existBox)) continue;

                if (!BoxesOverlap(newBox, existBox)) continue;

                if (!_nodePositions.TryGetValue(existingNode, out var existPos)) continue;
                if (!_nodePositions.TryGetValue(newNode, out var newPos)) continue;
                var dx = existPos.X - newPos.X;
                var dy = existPos.Y - newPos.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 1) { dx = 1; dy = 0; dist = 1; }

                var overlapX = Math.Min(newBox.Right - existBox.Left, existBox.Right - newBox.Left);
                var overlapY = Math.Min(newBox.Bottom - existBox.Top, existBox.Bottom - newBox.Top);
                var pushDist = Math.Min(overlapX, overlapY) + padding;

                var pushX = (dx / dist) * pushDist;
                var pushY = (dy / dist) * pushDist;

                if (displacements.TryGetValue(existingNode, out var existing))
                    displacements[existingNode] = (existing.X + pushX, existing.Y + pushY);
                else
                    displacements[existingNode] = (existPos.X + pushX, existPos.Y + pushY);
            }
        }

        // Remove new nodes from displacements — they were already snapped in place above
        foreach (var node in newNodes)
            displacements.Remove(node);

        return displacements;
    }

    #endregion

    #region Animation

    /// <summary>
    /// Smoothly animates nodes from their current positions to target positions.
    /// Blocks all interaction until animation completes.
    /// </summary>
    void AnimateNodesToTargets(Dictionary<Border, (double X, double Y)> targets, Action? onComplete = null)
    {
        if (targets.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        // If already animating, snap current animation to end first
        SnapAnimationToEnd();

        _isAnimating = true;
        _animProgress = 0;
        _animCompleteCallback = onComplete;
        _animStartPositions.Clear();
        _animTargetPositions.Clear();
        _animNodeSizes.Clear();

        foreach (var (node, target) in targets)
        {
            if (!_nodePositions.TryGetValue(node, out var current)) continue;
            _animStartPositions[node] = current;
            _animTargetPositions[node] = target;
            _animNodeSizes[node] = node.DesiredSize;
        }

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimIntervalMs) };
        _animTimer.Tick += AnimationTick;
        _animTimer.Start();
    }

    void AnimationTick(object? sender, EventArgs e)
    {
        _animProgress += AnimIntervalMs / AnimDurationMs;

        if (_animProgress >= 1.0)
        {
            SnapAnimationToEnd();
            return;
        }

        // Ease-out cubic: 1 - (1-t)^3
        var t = 1.0 - Math.Pow(1.0 - _animProgress, 3);

        foreach (var (node, start) in _animStartPositions)
        {
            if (!_animTargetPositions.TryGetValue(node, out var target)) continue;
            if (!_animNodeSizes.TryGetValue(node, out var size)) continue;
            var x = start.X + (target.X - start.X) * t;
            var y = start.Y + (target.Y - start.Y) * t;

            Canvas.SetLeft(node, x - size.Width / 2);
            Canvas.SetTop(node, y - size.Height / 2);
            UpdateEdgesForNode(node, x, y);
            _nodePositions[node] = (x, y);
        }
    }

    void SnapAnimationToEnd()
    {
        if (_animTimer != null)
        {
            _animTimer.Stop();
            _animTimer.Tick -= AnimationTick;
            _animTimer = null;
        }

        // Snap all nodes to final positions
        foreach (var (node, target) in _animTargetPositions)
        {
            if (!_nodePositions.ContainsKey(node)) continue;
            var size = _animNodeSizes.TryGetValue(node, out var cached) ? cached : node.DesiredSize;
            Canvas.SetLeft(node, target.X - size.Width / 2);
            Canvas.SetTop(node, target.Y - size.Height / 2);
            UpdateEdgesForNode(node, target.X, target.Y);
            _nodePositions[node] = target;
        }

        _animStartPositions.Clear();
        _animTargetPositions.Clear();
        _animNodeSizes.Clear();
        _isAnimating = false;

        var callback = _animCompleteCallback;
        _animCompleteCallback = null;
        callback?.Invoke();
    }

    #endregion

    #region Pan/Zoom

    void CanvasHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isAnimating) { e.Handled = true; return; }
        var props = e.GetCurrentPoint(CanvasHost).Properties;

        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            e.Pointer.Capture(CanvasHost);
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed && !_isDraggingNode)
        {
            // Only start selection if clicking on empty canvas, not on a node
            if (e.Source is Control source && IsInsideGraphNode(source))
                return;

            // Left-click on empty canvas area starts rectangular selection
            _isRectSelecting = true;
            _rectSelectStart = e.GetPosition(GraphCanvas);

            // Create selection rectangle visual
            _selectionRect = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#6f96bf"), 0.15),
                BorderBrush = new SolidColorBrush(Color.Parse("#6f96bf"), 0.6),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Width = 0,
                Height = 0
            };
            Canvas.SetLeft(_selectionRect, _rectSelectStart.X);
            Canvas.SetTop(_selectionRect, _rectSelectStart.Y);
            GraphCanvas.Children.Add(_selectionRect);

            e.Pointer.Capture(CanvasHost);
            e.Handled = true;
        }
    }

    void CanvasHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(this);
            var dx = pos.X - _panStart.X;
            var dy = pos.Y - _panStart.Y;
            _panStart = pos;

            // Append translation in screen space
            _graphTransform.Matrix *= Matrix.CreateTranslation(dx, dy);
            e.Handled = true;
        }
        else if (_isRectSelecting && _selectionRect != null)
        {
            var pos = e.GetPosition(GraphCanvas);
            var x = Math.Min(pos.X, _rectSelectStart.X);
            var y = Math.Min(pos.Y, _rectSelectStart.Y);
            var w = Math.Abs(pos.X - _rectSelectStart.X);
            var h = Math.Abs(pos.Y - _rectSelectStart.Y);

            Canvas.SetLeft(_selectionRect, x);
            Canvas.SetTop(_selectionRect, y);
            _selectionRect.Width = w;
            _selectionRect.Height = h;
            e.Handled = true;
        }
    }

    void CanvasHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else if (_isRectSelecting && _selectionRect != null)
        {
            _isRectSelecting = false;

            var rectLeft = Canvas.GetLeft(_selectionRect);
            var rectTop = Canvas.GetTop(_selectionRect);
            var rectRight = rectLeft + _selectionRect.Width;
            var rectBottom = rectTop + _selectionRect.Height;

            // Remove selection rectangle visual
            GraphCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;

            // If rect is too small, treat as click on empty area = clear selection
            if (rectRight - rectLeft < 5 && rectBottom - rectTop < 5)
            {
                ClearSelection();
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }

            // Find all nodes within the rectangle
            ClearSelection();
            foreach (var node in _nodeElements)
            {
                if (!_nodePositions.TryGetValue(node, out var pos)) continue;
                if (pos.X >= rectLeft && pos.X <= rectRight && pos.Y >= rectTop && pos.Y <= rectBottom)
                    SelectNode(node);
            }

            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    void CanvasHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var testZoom = _zoomLevel * delta;
        if (testZoom < ZoomMin || testZoom > ZoomMax) { e.Handled = true; return; }

        // Zoom centered on cursor position in host (screen) space
        // Formula: newTranslation = cursorPos - (cursorPos - oldTranslation) * delta
        var cursor = e.GetPosition(CanvasHost);
        var m = _graphTransform.Matrix;

        var newM31 = cursor.X - (cursor.X - m.M31) * delta;
        var newM32 = cursor.Y - (cursor.Y - m.M32) * delta;

        _graphTransform.Matrix = new Matrix(m.M11 * delta, 0, 0, m.M22 * delta, newM31, newM32);
        _zoomLevel = testZoom;

        e.Handled = true;
    }

    void ResetView_Click(object? sender, RoutedEventArgs e) => ResetView();

    void ResetView()
    {
        _zoomLevel = 1.0;
        var hostBounds = CanvasHost.Bounds;
        var cx = hostBounds.Width > 0 ? hostBounds.Width / 2 : 450;
        var cy = hostBounds.Height > 0 ? hostBounds.Height / 2 : 350;
        _graphTransform.Matrix = Matrix.CreateTranslation(cx, cy);
    }

    #endregion

    #region Node Drag & Selection

    void SetupNodeDrag(Border node)
    {
        node.PointerPressed += Node_PointerPressed;
        node.PointerMoved += Node_PointerMoved;
        node.PointerReleased += Node_PointerReleased;
    }

    void Node_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isAnimating) { e.Handled = true; return; }
        if (sender is not Border node) return;
        var props = e.GetCurrentPoint(node).Properties;
        if (!props.IsLeftButtonPressed) return;

        // Clicking a single node: if it's not selected, clear selection and drag just this node
        // If it IS selected (part of multi-select), drag all selected nodes together
        if (!_selectedNodes.Contains(node))
            ClearSelection();

        _isDraggingNode = true;
        _draggedNode = node;
        _dragStart = e.GetPosition(GraphCanvas);
        _dragNodeStartX = Canvas.GetLeft(node);
        _dragNodeStartY = Canvas.GetTop(node);

        // Store start positions for all selected nodes for multi-drag
        _multiDragStartPositions.Clear();
        foreach (var sel in _selectedNodes)
        {
            if (sel != node)
                _multiDragStartPositions[sel] = (Canvas.GetLeft(sel), Canvas.GetTop(sel));
        }

        e.Pointer.Capture(node);
        e.Handled = true;
    }

    void Node_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingNode || _draggedNode == null) return;

        var pos = e.GetPosition(GraphCanvas);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;

        // Move the dragged node
        MoveNodeBy(_draggedNode, _dragNodeStartX + dx, _dragNodeStartY + dy);

        // Move all other selected nodes by the same delta
        foreach (var (sel, startPos) in _multiDragStartPositions)
            MoveNodeBy(sel, startPos.X + dx, startPos.Y + dy);

        e.Handled = true;
    }

    void MoveNodeBy(Border node, double newLeft, double newTop)
    {
        var size = node.DesiredSize;
        var newCenterX = newLeft + size.Width / 2;
        var newCenterY = newTop + size.Height / 2;
        UpdateEdgesForNode(node, newCenterX, newCenterY);
        _nodePositions[node] = (newCenterX, newCenterY);
        Canvas.SetLeft(node, newLeft);
        Canvas.SetTop(node, newTop);
    }

    void Node_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingNode)
        {
            _isDraggingNode = false;
            _draggedNode = null;
            _multiDragStartPositions.Clear();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    void SelectNode(Border node)
    {
        if (!_selectedNodes.Add(node)) return;
        node.Opacity = 0.8;
        node.BorderBrush = new SolidColorBrush(Color.Parse("#6f96bf"));
    }

    void DeselectNode(Border node)
    {
        if (!_selectedNodes.Remove(node)) return;
        node.Opacity = 1.0;

        // Restore original border brush
        if (node.Tag is DependencyReference r)
        {
            var color = GetRefTypeColor(r.Type);
            node.BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(color, 0),
                    new GradientStop(Color.Parse("#333333"), 0.15)
                }
            };
        }
        else if (node.Tag is FileIndexEntry)
        {
            // Match node — restore its subtle color border
            var (matchColor, _) = GetMatchFileStyle(((FileIndexEntry)node.Tag).FileName);
            node.BorderBrush = new SolidColorBrush(matchColor, 0.4);
        }
        else
        {
            // Center node
            node.BorderBrush = new SolidColorBrush(CenterBorder);
        }
    }

    void ClearSelection()
    {
        foreach (var node in _selectedNodes.ToList())
            DeselectNode(node);
        _selectedNodes.Clear();
    }

    #endregion

    #region Interaction

    async void Node_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_isAnimating) return;
        // Don't navigate when double-tapping buttons (Hide/Show, Play, Preview, etc.)
        if (IsInsideButton(e.Source as Control))
            return;

        FileIndexEntry? entry = null;
        DependencyReference? depRef = null;

        if (sender is Border border)
        {
            if (border.Tag is FileIndexEntry match)
                entry = match;
            else if (border.Tag is DependencyReference reference && reference.Resolved.Count > 0)
            {
                entry = reference.Resolved[0];
                depRef = reference;
            }
        }

        if (entry != null)
        {
            await NavigateToEntryAsync(entry);

            // Highlight the reference value in the previewed document (matching DependenciesWindow behavior)
            if (depRef != null && _mainWindow != null)
            {
                if (depRef.Type is DependencyRefType.StringKey or DependencyRefType.SoundsetName)
                    await _mainWindow.HighlightTextInPreviewAsync(depRef.RawValue);
                else if (depRef.Type == DependencyRefType.FilePath && depRef.SourceTag == "sound")
                    await _mainWindow.HighlightTextInPreviewAsync(depRef.RawValue);
            }
        }
    }

    async void PreviewImage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileIndexEntry entry) return;
        if (_mainWindow == null) return;

        // Find the parent node Border to insert the preview into
        var parentNode = FindParentBorder(btn);
        if (parentNode == null) return;

        btn.IsEnabled = false;

        try
        {
            if (IsDdtOrImage(entry.FileName))
            {
                await LoadPreviewIntoNode(parentNode, entry);
            }
            else if (IsTmm(entry.FileName))
            {
                await LoadTmmPreviewIntoNode(parentNode, entry);
            }
        }
        catch { }
    }

    static bool IsInsideButton(Control? control)
    {
        while (control != null)
        {
            if (control is Button) return true;
            control = control.Parent as Control;
        }
        return false;
    }

    bool IsInsideGraphNode(Control? control)
    {
        while (control != null)
        {
            if (control is Border b && _nodeElementsSet.Contains(b))
                return true;

            control = control.Parent as Control;
        }
        return false;
    }

    static Border? FindParentBorder(Control control)
    {
        var parent = control.Parent;
        while (parent != null)
        {
            if (parent is Border b && (b.Tag is FileIndexEntry || b.Tag is DependencyReference))
                return b;
            parent = parent.Parent;
        }
        return null;
    }

    async Task LoadPreviewIntoNode(Border node, FileIndexEntry entry)
    {
        var bitmap = await LoadImageBitmapAsync(entry, 128, _thumbnailCts?.Token ?? CancellationToken.None);
        if (bitmap == null) return;

        _previewBitmaps.Add(bitmap);
        AppendPreviewToNode(node, new Avalonia.Controls.Image
        {
            Source = bitmap,
            Width = 96, Height = 96,
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    async Task LoadTmmPreviewIntoNode(Border node, FileIndexEntry entry)
    {
        if (_mainWindow == null) return;

        using var data = await _mainWindow.ReadFromIndexEntryPooledAsync(entry);
        if (data == null) return;

        var tmm = new TmmFile(data.Memory);
        if (!tmm.Parsed) return;

        // Find companion .data file in file index
        var dataFileName = entry.FileName + ".data";
        PooledBuffer? companionData = null;
        if (_fileIndex != null)
        {
            foreach (var ie in _fileIndex.Find(dataFileName))
            {
                companionData = await _mainWindow.ReadFromIndexEntryPooledAsync(ie);
                if (companionData != null) break;
            }
        }

        if (companionData == null)
        {
            // Fallback: show text info only
            AppendPreviewToNode(node, new TextBlock
            {
                Text = $"TMM v{tmm.Version} \u2014 {tmm.NumBones} bones, {tmm.NumMaterials} mats",
                Foreground = new SolidColorBrush(TmmColor),
                FontSize = 9,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        using (companionData)
        {
            var mesh = MeshDataBuilder.BuildFromTmm(data.Memory, companionData.Memory);
            if (mesh == null)
            {
                AppendPreviewToNode(node, new TextBlock
                {
                    Text = $"TMM v{tmm.Version} (mesh build failed)",
                    Foreground = new SolidColorBrush(TmmColor),
                    FontSize = 9,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            var bitmap = RenderMeshPreview(mesh, 192);
            if (bitmap != null)
            {
                _previewBitmaps.Add(bitmap);
                AppendPreviewToNode(node, new Avalonia.Controls.Image
                {
                    Source = bitmap,
                    Width = 96, Height = 96,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }
    }

    static void AppendPreviewToNode(Border node, Control preview)
    {
        if (node.Child is not StackPanel stack) return;

        // Ensure vertical layout to append below
        if (stack.Orientation == Avalonia.Layout.Orientation.Horizontal)
        {
            var existingContent = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4
            };
            while (stack.Children.Count > 0)
            {
                var child = stack.Children[0];
                stack.Children.RemoveAt(0);
                existingContent.Children.Add(child);
            }
            stack.Orientation = Avalonia.Layout.Orientation.Vertical;
            stack.Children.Add(existingContent);
        }

        stack.Children.Add(preview);
    }

    async Task NavigateToEntryAsync(FileIndexEntry entry)
    {
        if (_mainWindow == null) return;

        if (entry.Source == FileIndexSource.BarEntry && entry.BarFilePath != null && entry.EntryRelativePath != null)
        {
            var barRelPath = entry.BarFilePath;
            if (Directory.Exists(_mainWindow.RootDirectory) && barRelPath.StartsWith(_mainWindow.RootDirectory))
                barRelPath = IOPath.GetRelativePath(_mainWindow.RootDirectory, barRelPath);

            await _mainWindow.NavigateToBarEntryAsync(barRelPath, entry.EntryRelativePath);
        }
        else
        {
            var fullRelPath = entry.FullRelativePath;
            var rootRelevantPath = _mainWindow.RootFileRootPath;
            if (rootRelevantPath != "-" && fullRelPath.StartsWith(rootRelevantPath, StringComparison.OrdinalIgnoreCase))
                fullRelPath = fullRelPath[rootRelevantPath.Length..];

            _mainWindow.NavigateToRootFile(fullRelPath);
        }
    }

    async void PlaySoundset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DependencyReference reference) return;
        if (_mainWindow == null || !Directory.Exists(_mainWindow.RootDirectory)) return;

        // Stop any previous playback immediately (allows re-clicking to restart)
        StopPlayback();

        // Find the bank file for this soundset — DependencyFinder already resolves
        // soundset → soundset file + bank file via ResolveSoundsetName, so check resolved entries first
        FileIndexEntry? bankEntry = reference.Resolved.FirstOrDefault(
            r => r.FileName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase));

        // Fallback: use SoundsetIndex
        if (bankEntry == null)
        {
            var ssEntry = _mainWindow.SoundsetIndex?.Find(reference.RawValue);
            bankEntry = ssEntry?.BankFile;
        }

        // Fallback: extract culture from resolved soundset file name
        if (bankEntry == null && _fileIndex != null)
        {
            foreach (var resolved in reference.Resolved)
            {
                var culture = SoundsetIndex.ExtractCulture(resolved.FileName);
                if (culture == null) continue;
                var matches = _fileIndex.Find(culture + ".bank");
                if (matches.Count > 0) { bankEntry = matches[0]; break; }
            }
        }

        if (bankEntry == null) return;

        try
        {
            string bankDiskPath;

            if (bankEntry.Source == FileIndexSource.RootFile)
            {
                var bankRelPath = bankEntry.FullRelativePath;
                var rootRelevantPath = _mainWindow.RootFileRootPath;
                if (rootRelevantPath != "-" && bankRelPath.StartsWith(rootRelevantPath, StringComparison.OrdinalIgnoreCase))
                    bankRelPath = bankRelPath[rootRelevantPath.Length..];

                bankDiskPath = IOPath.Combine(_mainWindow.RootDirectory, bankRelPath);
            }
            else
            {
                // BAR entry: extract to temp file (skip if already extracted)
                var tempDir = IOPath.Combine(IOPath.GetTempPath(), "CryBar_FMOD");
                Directory.CreateDirectory(tempDir);
                bankDiskPath = IOPath.Combine(tempDir, bankEntry.FileName);

                if (!File.Exists(bankDiskPath))
                {
                    using var data = await _mainWindow.ReadFromIndexEntryPooledAsync(bankEntry);
                    if (data == null) return;
                    await File.WriteAllBytesAsync(bankDiskPath, data.Span.ToArray());
                }

                // Also extract Master.bank and Master.strings.bank if not already present
                if (_fileIndex != null)
                {
                    foreach (var masterName in new[] { "Master.bank", "Master.strings.bank" })
                    {
                        var masterPath = IOPath.Combine(tempDir, masterName);
                        if (File.Exists(masterPath)) continue;

                        var masterEntries = _fileIndex.Find(masterName);
                        if (masterEntries.Count > 0)
                        {
                            using var masterData = await _mainWindow.ReadFromIndexEntryPooledAsync(masterEntries[0]);
                            if (masterData != null)
                                await File.WriteAllBytesAsync(masterPath, masterData.Span.ToArray());
                        }
                    }
                }
            }

            if (!File.Exists(bankDiskPath)) return;

            // Dispose previous bank before loading new one
            _playbackBank?.Dispose();
            _playbackBank = FMODBank.LoadBank(bankDiskPath);

            if (_playbackBank?.Events == null || _playbackBank.Events.Length == 0) return;

            // Find event matching the soundset name (event path ends with /SoundsetName)
            var fmodEvent = _playbackBank.Events.FirstOrDefault(
                ev => ev.Path.EndsWith("/" + reference.RawValue, StringComparison.OrdinalIgnoreCase));

            if (fmodEvent == null) return;

            _playbackCts = new CancellationTokenSource();
            await fmodEvent.Play(_playbackCts.Token);
        }
        catch { }
    }

    void StopPlayback()
    {
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = null;
    }

    void DisposePlaybackBank()
    {
        StopPlayback();
        _playbackBank?.Dispose();
        _playbackBank = null;
    }

    async void RecursiveLoad_Click(object? sender, RoutedEventArgs e)
    {
        if (_isAnimating) return;
        if (sender is not Button btn || btn.Tag is not DependencyReference reference) return;
        if (reference.Resolved.Count == 0) return;
        await ExpandOrCollapseAsync(btn, reference.Resolved[0], reference.RawValue);
    }

    async void MatchExpandCollapse_Click(object? sender, RoutedEventArgs e)
    {
        if (_isAnimating) return;
        if (sender is not Button btn || btn.Tag is not FileIndexEntry entry) return;
        await ExpandOrCollapseAsync(btn, entry, entry.FullRelativePath);
    }

    async Task ExpandOrCollapseAsync(Button btn, FileIndexEntry indexEntry, string pathKey)
    {
        if (_mainWindow == null || _fileIndex == null) return;

        var parentNode = FindParentBorder(btn);
        if (parentNode == null) return;

        // Collapse if already expanded
        if (_expansionChildren.TryGetValue(parentNode, out var children))
        {
            var collapsedNodeCount = CountExpansionNodes(parentNode);
            CollapseChildren(parentNode, children);
            _expansionChildren.Remove(parentNode);
            _visitedPaths.Remove(pathKey);
            _expandedRefCount = Math.Max(0, _expandedRefCount - collapsedNodeCount);
            UpdateStatusText();
            btn.Content = "Show";
            ToolTip.SetTip(btn, "Show dependencies");
            return;
        }

        if (!_nodePositions.TryGetValue(parentNode, out var parentPos)) return;

        if (!_visitedPaths.Add(pathKey))
        {
            btn.Content = "(loop)";
            btn.IsEnabled = false;
            return;
        }

        btn.Content = "...";
        btn.IsEnabled = false;

        try
        {
            using var data = await _mainWindow.ReadFromIndexEntryPooledAsync(indexEntry);
            if (data == null) { btn.Content = "(read failed)"; btn.IsEnabled = true; return; }

            var result = await DependencyFinder.FindDependenciesForFileAsync(
                indexEntry.FullRelativePath, data, _fileIndex);

            var allRefs = result.GetAllReferences().ToList();
            if (allRefs.Count == 0) { btn.Content = "(empty)"; btn.IsEnabled = true; return; }

            btn.Content = "Hide";
            btn.IsEnabled = true;
            ToolTip.SetTip(btn, "Hide dependencies");

            var spawnedControls = new List<Control>();
            var newNodeSet = new HashSet<Border>();

            double baseAngle = Math.Atan2(parentPos.Y, parentPos.X);

            // Pre-create and measure all expansion nodes
            var expansionNodes = new List<(DependencyReference Ref, Border Node, Color Color, Size Size)>();
            foreach (var r in allRefs)
            {
                var refColor = GetRefTypeColor(r.Type);
                var subNode = CreateReferenceNode(r, refColor);
                expansionNodes.Add((r, subNode, refColor, MeasureNode(subNode)));
            }

            const int ExpansionRadialThreshold = 12;

            if (allRefs.Count <= ExpansionRadialThreshold)
            {
                LayoutExpansionRadial(parentNode, parentPos, baseAngle, expansionNodes,
                    spawnedControls, newNodeSet);
            }
            else
            {
                LayoutExpansionColumnar(parentNode, parentPos, baseAngle, expansionNodes,
                    spawnedControls, newNodeSet);
            }

            _expansionChildren[parentNode] = spawnedControls;
            _expandedRefCount += spawnedControls.Count(c => c is Border);
            UpdateStatusText();

            // Push existing nodes away from newly placed ones to resolve overlaps
            var displacements = ComputeOverlapDisplacements(newNodeSet);
            if (displacements.Count > 0)
                AnimateNodesToTargets(displacements);
        }
        catch
        {
            btn.Content = "(error)";
            btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Recursively counts all Border nodes in an expansion tree (for status text tracking).
    /// </summary>
    int CountExpansionNodes(Border parentNode)
    {
        if (!_expansionChildren.TryGetValue(parentNode, out var children)) return 0;
        int count = 0;
        foreach (var child in children)
        {
            if (child is Border childNode)
            {
                count++;
                count += CountExpansionNodes(childNode);
            }
        }
        return count;
    }

    /// <summary>
    /// Radial arc layout for small expansions (≤12 refs). Fans out from parent node.
    /// </summary>
    void LayoutExpansionRadial(Border parentNode, (double X, double Y) parentPos, double baseAngle,
        List<(DependencyReference Ref, Border Node, Color Color, Size Size)> nodes,
        List<Control> spawnedControls, HashSet<Border> newNodeSet)
    {
        double maxWidth = nodes.Max(n => n.Size.Width);
        double effectiveWidth = maxWidth + 30;

        double subCircumference = nodes.Count * effectiveWidth;
        double subRadius = Math.Max(200, subCircumference / Math.PI);
        double arcSpan = Math.Min(Math.PI * 0.8, nodes.Count * 0.15 + 0.3);
        double startAngle = baseAngle - arcSpan / 2;

        for (int i = 0; i < nodes.Count; i++)
        {
            var info = nodes[i];
            double angle = nodes.Count == 1
                ? baseAngle
                : startAngle + i * arcSpan / Math.Max(1, nodes.Count - 1);
            double sx = parentPos.X + subRadius * Math.Cos(angle);
            double sy = parentPos.Y + subRadius * Math.Sin(angle);

            PlaceNode(info.Node, sx, sy);
            spawnedControls.Add(info.Node);
            newNodeSet.Add(info.Node);

            var edge = CreateEdge(parentPos.X, parentPos.Y, sx, sy, info.Color, 1.0);
            ConnectEdge(parentNode, edge);
            ConnectEdge(info.Node, edge);
            spawnedControls.Add(edge);

            var visibleMatches = GetVisibleMatches(info.Ref);
            if (visibleMatches.Count > 0)
                LayoutMatchSubNodes(info.Node, visibleMatches, sx, sy, angle, spawnedControls);
        }
    }

    /// <summary>
    /// Columnar layout for large expansions (>12 refs). Groups by type, stacks in columns
    /// radiating outward from the parent node.
    /// </summary>
    void LayoutExpansionColumnar(Border parentNode, (double X, double Y) parentPos, double baseAngle,
        List<(DependencyReference Ref, Border Node, Color Color, Size Size)> nodes,
        List<Control> spawnedControls, HashSet<Border> newNodeSet)
    {
        var grouped = nodes.GroupBy(n => n.Ref.Type).OrderBy(g => g.Key).ToList();
        int groupCount = grouped.Count;

        // Fan columns around the base angle
        double totalFanAngle = Math.Min(Math.PI * 0.8, groupCount * 0.5 + 0.3);
        double fanStart = baseAngle - totalFanAngle / 2;

        for (int g = 0; g < groupCount; g++)
        {
            var typeGroup = grouped[g].ToList();
            double groupAngle = groupCount == 1
                ? baseAngle
                : fanStart + g * totalFanAngle / Math.Max(1, groupCount - 1);

            double dirX = Math.Cos(groupAngle);
            double dirY = Math.Sin(groupAngle);
            double perpX = -dirY;
            double perpY = dirX;

            // Compute column dimensions from measured sizes
            double maxWidth = typeGroup.Max(n => n.Size.Width);
            double verticalSpacing = 8;
            double totalHeight = typeGroup.Sum(n => n.Size.Height + verticalSpacing) - verticalSpacing;

            bool hasSubNodes = typeGroup.Any(n => GetVisibleMatches(n.Ref).Count > 0);
            double colDistance = 200 + (hasSubNodes ? maxWidth * 0.5 : 0);

            double colCenterX = parentPos.X + dirX * colDistance;
            double colCenterY = parentPos.Y + dirY * colDistance;

            double stackOffset = -totalHeight / 2;

            for (int i = 0; i < typeGroup.Count; i++)
            {
                var info = typeGroup[i];
                double perpOffset = stackOffset + info.Size.Height / 2;
                double nx = colCenterX + perpX * perpOffset;
                double ny = colCenterY + perpY * perpOffset;

                PlaceNode(info.Node, nx, ny);
                spawnedControls.Add(info.Node);
                newNodeSet.Add(info.Node);

                var edge = CreateEdge(parentPos.X, parentPos.Y, nx, ny, info.Color, 1.0);
                ConnectEdge(parentNode, edge);
                ConnectEdge(info.Node, edge);
                spawnedControls.Add(edge);

                var visibleMatches = GetVisibleMatches(info.Ref);
                if (visibleMatches.Count > 0)
                    LayoutMatchSubNodes(info.Node, visibleMatches, nx, ny, groupAngle, spawnedControls);

                stackOffset += info.Size.Height + verticalSpacing;
            }
        }
    }

    void CollapseChildren(Border parentNode, List<Control> children)
    {
        // Recursively collapse any sub-expansions first
        foreach (var child in children)
        {
            if (child is Border childNode && _expansionChildren.TryGetValue(childNode, out var subChildren))
            {
                CollapseChildren(childNode, subChildren);
                _expansionChildren.Remove(childNode);
            }
        }

        // Remove all spawned controls from canvas and tracking
        foreach (var child in children)
        {
            GraphCanvas.Children.Remove(child);

            if (child is Border node)
            {
                _nodeElements.Remove(node);
                _nodeElementsSet.Remove(node);
                _nodePositions.Remove(node);
                _animStartPositions.Remove(node);
                _animTargetPositions.Remove(node);
                if (_nodeEdges.Remove(node, out var nodeEdgeList))
                {
                    // Clean edge references from connected nodes
                    foreach (var edge in nodeEdgeList)
                    {
                        foreach (var (otherNode, otherEdges) in _nodeEdges)
                            otherEdges.Remove(edge);
                    }
                }
            }
            else if (child is Line line)
            {
                _edgeElements.Remove(line);
                // Remove from parent's edge list
                if (_nodeEdges.TryGetValue(parentNode, out var parentEdges))
                    parentEdges.Remove(line);
            }
        }
    }

    #endregion

    #region Image Loading

    /// <summary>
    /// Loads an image (DDT or standard format) from a file index entry and returns an Avalonia Bitmap.
    /// </summary>
    async Task<Bitmap?> LoadImageBitmapAsync(FileIndexEntry entry, int maxSize, CancellationToken ct)
    {
        if (_mainWindow == null) return null;

        using var data = await _mainWindow.ReadFromIndexEntryPooledAsync(entry);
        if (data == null || ct.IsCancellationRequested) return null;

        SixLabors.ImageSharp.Image? image = null;
        try
        {
            var ext = IOPath.GetExtension(entry.FileName).ToLowerInvariant();
            if (ext == ".ddt")
            {
                var ddt = new DDTImage(data.Memory);
                if (!ddt.ParseHeader()) return null;
                image = await BarFormatConverter.ParseDDT(ddt, max_resolution: maxSize, token: ct);
            }
            else
            {
                image = SixLabors.ImageSharp.Image.Load(data.Span);
            }

            if (image == null || ct.IsCancellationRequested) return null;

            using var ms = new MemoryStream();
            await image.SaveAsync(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
            }, ct);

            if (ct.IsCancellationRequested) return null;
            ms.Seek(0, SeekOrigin.Begin);
            return new Bitmap(ms);
        }
        finally
        {
            image?.Dispose();
        }
    }

    static bool IsDdtOrImage(string fileName)
    {
        var ext = IOPath.GetExtension(fileName).ToLowerInvariant();
        return ext is ".ddt" or ".jpg" or ".jpeg" or ".png" or ".tga"
            or ".gif" or ".webp" or ".avif" or ".jpx" or ".bmp";
    }

    static bool IsTmm(string fileName)
    {
        return fileName.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the file can be recursively expanded (parsed for further dependencies).
    /// Matches any XMB file (including .material.XMB, .soundset.XMB, etc.), plain XML,
    /// and other text-based formats that DependencyFinder can parse.
    /// Excludes binary-only formats (images, .tmm.data, .bank).
    /// </summary>
    static bool CanExpandRecursively(string fileName)
    {
        if (fileName.EndsWith(".xmb", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".material", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".tactics", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".soundset", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    #endregion

    #region TMM Software Render

    /// <summary>
    /// Renders a TMM mesh to a static bitmap using CPU rasterization.
    /// Uses OrbitCamera defaults: azimuth=142, elevation=23, fit-to-sphere.
    /// </summary>
    static Bitmap? RenderMeshPreview(PreviewMeshData mesh, int size = 96)
    {
        var camera = new OrbitCamera();
        camera.FitToSphere(mesh.CenterX, mesh.CenterY, mesh.CenterZ, mesh.Radius);

        var view = camera.GetViewMatrix(out var eye);
        var proj = camera.GetProjectionMatrix(1f); // square aspect
        var mvp = view * proj; // row-vector: clip = worldPos * mvp

        var target = new Vector3(camera.TargetX, camera.TargetY, camera.TargetZ);
        var lightDir = Vector3.Normalize(eye - target);

        var pixels = new byte[size * size * 4]; // RGBA
        var zbuf = new float[size * size];

        // Dark background
        for (int i = 0; i < size * size; i++)
        {
            pixels[i * 4] = 0x1e;
            pixels[i * 4 + 1] = 0x1e;
            pixels[i * 4 + 2] = 0x1e;
            pixels[i * 4 + 3] = 0xFF;
            zbuf[i] = float.MaxValue;
        }

        var verts = mesh.Vertices;
        int vertCount = verts.Length / 8;

        // Project all vertices to screen space
        var screen = new (float x, float y, float z, bool visible)[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            int off = i * 8;
            var worldPos = new Vector4(verts[off], verts[off + 1], verts[off + 2], 1f);
            var clip = Vector4.Transform(worldPos, mvp);

            if (clip.W <= 0.001f) { screen[i] = (0, 0, 0, false); continue; }

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float ndcZ = clip.Z / clip.W;

            screen[i] = (
                (ndcX + 1f) * 0.5f * size,
                (1f - ndcY) * 0.5f * size, // flip Y
                ndcZ,
                true
            );
        }

        // Rasterize triangles
        var indices = mesh.Indices;
        foreach (var (groupOffset, groupCount) in mesh.DrawGroups)
        {
            for (int i = groupOffset; i + 2 < groupOffset + groupCount; i += 3)
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                var (x0, y0, z0, v0) = screen[i0];
                var (x1, y1, z1, v1) = screen[i1];
                var (x2, y2, z2, v2) = screen[i2];

                if (!v0 || !v1 || !v2) continue;

                // Face normal from first vertex normal (flat shading approximation)
                int off0 = (int)i0 * 8;
                var faceNormal = Vector3.Normalize(new Vector3(
                    verts[off0 + 3], verts[off0 + 4], verts[off0 + 5]));
                float diffuse = Math.Max(Vector3.Dot(faceNormal, lightDir), 0f);
                byte shade = (byte)Math.Clamp((int)((0.25f + 0.75f * diffuse) * 191f), 0, 255);

                // Bounding box
                int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
                int maxX = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
                int maxY = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));

                // Edge function rasterization
                float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                if (MathF.Abs(area) < 0.001f) continue; // degenerate
                float invArea = 1f / area;

                for (int py = minY; py <= maxY; py++)
                {
                    for (int px = minX; px <= maxX; px++)
                    {
                        float cx = px + 0.5f, cy = py + 0.5f;
                        float w0 = ((x1 - cx) * (y2 - cy) - (x2 - cx) * (y1 - cy)) * invArea;
                        float w1 = ((x2 - cx) * (y0 - cy) - (x0 - cx) * (y2 - cy)) * invArea;
                        float w2 = 1f - w0 - w1;

                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                        float z = w0 * z0 + w1 * z1 + w2 * z2;
                        int idx = py * size + px;
                        if (z < zbuf[idx])
                        {
                            zbuf[idx] = z;
                            int pidx = idx * 4;
                            pixels[pidx] = shade;
                            pixels[pidx + 1] = shade;
                            pixels[pidx + 2] = shade;
                            pixels[pidx + 3] = 0xFF;
                        }
                    }
                }
            }
        }

        // Convert to Avalonia Bitmap via ImageSharp PNG
        try
        {
            using var img = new SixLabors.ImageSharp.Image<Rgba32>(size, size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = (y * size + x) * 4;
                    img[x, y] = new Rgba32(pixels[idx], pixels[idx + 1], pixels[idx + 2], pixels[idx + 3]);
                }
            }

            using var ms = new MemoryStream();
            img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
            });
            ms.Seek(0, SeekOrigin.Begin);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region String Translation

    async Task LoadStringTranslationAsync(TextBlock target, string key)
    {
        if (_mainWindow == null) return;

        try
        {
            var translation = await _mainWindow.LookupStringKeyAsync(key);
            if (translation != null)
            {
                if (translation.Length > 50)
                    translation = translation[..47] + "...";
                target.Text = $"\"{translation}\"";
            }
            else
            {
                target.Text = "(no translation)";
            }
        }
        catch
        {
            target.Text = "(lookup failed)";
        }
    }

    #endregion

    #region Style Helpers

    static Color GetRefTypeColor(DependencyRefType type) => type switch
    {
        DependencyRefType.StringKey => StringKeyColor,
        DependencyRefType.SoundsetName => SoundsetColor,
        _ => FilePathColor
    };

    static MaterialIconKind GetRefTypeIcon(DependencyRefType type) => type switch
    {
        DependencyRefType.FilePath => MaterialIconKind.FileOutline,
        DependencyRefType.StringKey => MaterialIconKind.FormatLetterCase,
        DependencyRefType.SoundsetName => MaterialIconKind.MusicNote,
        _ => MaterialIconKind.FileOutline
    };

    static (Color color, MaterialIconKind icon) GetMatchFileStyle(string fileName)
    {
        if (fileName.EndsWith(".tmm.data", StringComparison.OrdinalIgnoreCase)) return (TmmDataColor, MaterialIconKind.DatabaseOutline);
        if (fileName.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase)) return (TmmColor, MaterialIconKind.CubeOutline);
        if (fileName.EndsWith(".ddt", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            return (TextureColor, MaterialIconKind.ImageOutline);
        if (fileName.EndsWith(".material", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".material.xmb", StringComparison.OrdinalIgnoreCase))
            return (MaterialColor, MaterialIconKind.Palette);
        if (fileName.EndsWith(".fbximport", StringComparison.OrdinalIgnoreCase))
            return (FbxImportColor, MaterialIconKind.Import);
        if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".xmb", StringComparison.OrdinalIgnoreCase))
            return (XmlColor, MaterialIconKind.CodeTags);
        return (GenericColor, MaterialIconKind.FileOutline);
    }

    #endregion

    void DisposeBitmaps()
    {
        foreach (var bitmap in _previewBitmaps)
            bitmap.Dispose();
        _previewBitmaps.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        SnapAnimationToEnd();
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        DisposePlaybackBank();
        DisposeBitmaps();
        base.OnClosed(e);
    }
}
