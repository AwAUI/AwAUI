using System;

namespace AwAUI.Win32;

// 本文件属于 Input/ 纯逻辑：只含 UI Automation 的数字常量与枚举映射，不引用任何 Win32 互操作，
// 由 AwAUI.Platform.UnitTests 以源码链接方式在任意系统上编译测试（design/11）。

/// <summary>UI Automation 控件类型 ID（UIA_*ControlTypeId）。</summary>
internal static class Win32UiaControlTypeIds
{
    public const int Button = 50000;
    public const int Calendar = 50001;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Hyperlink = 50005;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int List = 50008;
    public const int Menu = 50009;
    public const int MenuBar = 50010;
    public const int MenuItem = 50011;
    public const int ProgressBar = 50012;
    public const int RadioButton = 50013;
    public const int ScrollBar = 50014;
    public const int Slider = 50015;
    public const int Spinner = 50016;
    public const int StatusBar = 50017;
    public const int Tab = 50018;
    public const int TabItem = 50019;
    public const int Text = 50020;
    public const int ToolBar = 50021;
    public const int ToolTip = 50022;
    public const int Tree = 50023;
    public const int TreeItem = 50024;
    public const int Custom = 50025;
    public const int Group = 50026;
    public const int Thumb = 50027;
    public const int DataGrid = 50028;
    public const int DataItem = 50029;
    public const int Document = 50030;
    public const int SplitButton = 50031;
    public const int Window = 50032;
    public const int Pane = 50033;
    public const int Header = 50034;
    public const int HeaderItem = 50035;
    public const int Table = 50036;
    public const int TitleBar = 50037;
    public const int Separator = 50038;
    public const int SemanticZoom = 50039;
    public const int AppBar = 50040;
}

/// <summary>UI Automation 控件模式 ID（UIA_*PatternId）。</summary>
internal static class Win32UiaPatternIds
{
    public const int Invoke = 10000;
    public const int Selection = 10001;
    public const int Value = 10002;
    public const int RangeValue = 10003;
    public const int Scroll = 10004;
    public const int ExpandCollapse = 10005;
    public const int Grid = 10006;
    public const int GridItem = 10007;
    public const int MultipleView = 10008;
    public const int Window = 10009;
    public const int SelectionItem = 10010;
    public const int Dock = 10011;
    public const int Table = 10012;
    public const int TableItem = 10013;
    public const int Text = 10014;
    public const int Toggle = 10015;
    public const int Transform = 10016;
    public const int ScrollItem = 10017;
    public const int LegacyIAccessible = 10018;
}

/// <summary>UI Automation 属性 ID（UIA_*PropertyId）。</summary>
internal static class Win32UiaPropertyIds
{
    public const int RuntimeId = 30000;
    public const int BoundingRectangle = 30001;
    public const int ProcessId = 30002;
    public const int ControlType = 30003;
    public const int LocalizedControlType = 30004;
    public const int Name = 30005;
    public const int AcceleratorKey = 30006;
    public const int AccessKey = 30007;
    public const int HasKeyboardFocus = 30008;
    public const int IsKeyboardFocusable = 30009;
    public const int IsEnabled = 30010;
    public const int AutomationId = 30011;
    public const int ClassName = 30012;
    public const int HelpText = 30013;
    public const int ClickablePoint = 30014;
    public const int Culture = 30015;
    public const int IsControlElement = 30016;
    public const int IsContentElement = 30017;
    public const int LabeledBy = 30018;
    public const int IsPassword = 30019;
    public const int NativeWindowHandle = 30020;
    public const int ItemType = 30021;
    public const int IsOffscreen = 30022;
    public const int Orientation = 30023;
    public const int FrameworkId = 30024;
    public const int IsRequiredForForm = 30025;
    public const int ItemStatus = 30026;

    public const int IsDockPatternAvailable = 30027;
    public const int IsExpandCollapsePatternAvailable = 30028;
    public const int IsGridItemPatternAvailable = 30029;
    public const int IsGridPatternAvailable = 30030;
    public const int IsInvokePatternAvailable = 30031;
    public const int IsMultipleViewPatternAvailable = 30032;
    public const int IsRangeValuePatternAvailable = 30033;
    public const int IsScrollPatternAvailable = 30034;
    public const int IsScrollItemPatternAvailable = 30035;
    public const int IsSelectionItemPatternAvailable = 30036;
    public const int IsSelectionPatternAvailable = 30037;
    public const int IsTablePatternAvailable = 30038;
    public const int IsTableItemPatternAvailable = 30039;
    public const int IsTextPatternAvailable = 30040;
    public const int IsTogglePatternAvailable = 30041;
    public const int IsValuePatternAvailable = 30043;
    public const int IsWindowPatternAvailable = 30044;

    public const int ValueValue = 30045;
    public const int ValueIsReadOnly = 30046;
    public const int RangeValueValue = 30047;
    public const int RangeValueIsReadOnly = 30048;
    public const int RangeValueMinimum = 30049;
    public const int RangeValueMaximum = 30050;
    public const int RangeValueLargeChange = 30051;
    public const int RangeValueSmallChange = 30052;
    public const int SelectionSelection = 30059;
    public const int SelectionCanSelectMultiple = 30060;
    public const int SelectionIsSelectionRequired = 30061;
    public const int GridRowCount = 30062;
    public const int GridColumnCount = 30063;
    public const int GridItemRow = 30064;
    public const int GridItemColumn = 30065;
    public const int GridItemRowSpan = 30066;
    public const int GridItemColumnSpan = 30067;
    public const int GridItemContainingGrid = 30068;
    public const int ExpandCollapseExpandCollapseState = 30070;
    public const int SelectionItemIsSelected = 30079;
    public const int SelectionItemSelectionContainer = 30080;
    public const int ToggleToggleState = 30086;
    public const int ControllerFor = 30104;
    public const int DescribedBy = 30105;
    public const int LiveSetting = 30135;
    public const int PositionInSet = 30152;
    public const int SizeOfSet = 30153;
    public const int Level = 30154;
    public const int HeadingLevel = 30173;
    public const int IsDialog = 30174;
}

/// <summary>UI Automation 事件 ID（UIA_*EventId）。</summary>
internal static class Win32UiaEventIds
{
    public const int StructureChanged = 20002;
    public const int AutomationPropertyChanged = 20004;
    public const int AutomationFocusChanged = 20005;
    public const int InvokeInvoked = 20009;
    public const int SelectionItemElementAddedToSelection = 20010;
    public const int SelectionItemElementRemovedFromSelection = 20011;
    public const int SelectionItemElementSelected = 20012;
    public const int SelectionInvalidated = 20013;
    public const int TextTextSelectionChanged = 20014;
    public const int TextTextChanged = 20015;
    public const int ToggleToggleStateChanged = 20016;
    public const int ExpandCollapseExpandCollapseStateChanged = 20017;
    public const int LiveRegionChanged = 20024;
    public const int Notification = 20035;
}

/// <summary>UI Automation 的杂项常量。</summary>
internal static class Win32UiaConstants
{
    /// <summary>运行时 ID 数组的第一个元素（UiaAppendRuntimeId）。</summary>
    public const int AppendRuntimeId = 0x2A;

    /// <summary>WM_GETOBJECT 的 lParam：UIA 根对象（UiaRootObjectId）。</summary>
    public const int RootObjectId = -25;

    public const uint WmGetObject = 0x003D;

    /// <summary>IRawElementProviderSimple::get_ProviderOptions 返回值。</summary>
    public const int ProviderOptionsServerSide = 0x2;
    public const int ProviderOptionsUseComThreading = 0x20;

    public const int ToggleStateOff = 0;
    public const int ToggleStateOn = 1;
    public const int ToggleStateIndeterminate = 2;

    public const int ExpandCollapseStateCollapsed = 0;
    public const int ExpandCollapseStateExpanded = 1;
    public const int ExpandCollapseStatePartiallyExpanded = 2;
    public const int ExpandCollapseStateLeafNode = 3;

    public const int OrientationNone = 0;
    public const int OrientationHorizontal = 1;
    public const int OrientationVertical = 2;

    public const int LiveSettingOff = 0;
    public const int LiveSettingPolite = 1;
    public const int LiveSettingAssertive = 2;

    public const int NavigateParent = 0;
    public const int NavigateNextSibling = 1;
    public const int NavigatePreviousSibling = 2;
    public const int NavigateFirstChild = 3;
    public const int NavigateLastChild = 4;

    public const int StructureChangeChildAdded = 0;
    public const int StructureChangeChildRemoved = 1;
    public const int StructureChangeChildrenInvalidated = 2;
    public const int StructureChangeChildrenBulkAdded = 3;
    public const int StructureChangeChildrenBulkRemoved = 4;
    public const int StructureChangeChildrenReordered = 5;

    public const int NotificationKindItemAdded = 0;
    public const int NotificationKindItemRemoved = 1;
    public const int NotificationKindActionCompleted = 2;
    public const int NotificationKindActionAborted = 3;
    public const int NotificationKindOther = 4;

    public const int NotificationProcessingImportantAll = 0;
    public const int NotificationProcessingImportantMostRecent = 1;
    public const int NotificationProcessingAll = 2;
    public const int NotificationProcessingMostRecent = 3;
    public const int NotificationProcessingCurrentThenMostRecent = 4;
}

/// <summary>快照节点可映射到的常用 UIA 控件模式（位掩码）。</summary>
[Flags]
internal enum Win32AccessibilityPattern
{
    None = 0,
    Invoke = 1 << 0,
    Toggle = 1 << 1,
    Value = 1 << 2,
    RangeValue = 1 << 3,
    ExpandCollapse = 1 << 4,
    SelectionItem = 1 << 5,
    Selection = 1 << 6,
    Grid = 1 << 7,
    GridItem = 1 << 8,
    ScrollItem = 1 << 9,
}
