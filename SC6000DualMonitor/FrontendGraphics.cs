using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SC6000DualMonitor
{
    // VM 4.4.50 Operation Interface graphical selection, not algorithm enable/disable.
    internal sealed class FrontendGraphics
    {
        private readonly object _view;
        private readonly PropertyInfo _path, _source;
        private readonly string[] _targets;

        internal FrontendGraphics(object view, IEnumerable<string> targets)
        {
            _view = view;
            _path = view.GetType().GetProperty("ShapeObjectPath");
            _source = view.GetType().GetProperty("ShapeObjectPathSource");
            if (_path == null || _source == null || !_source.CanWrite)
                throw new InvalidOperationException("SDK 그래픽 선택 속성을 찾지 못했습니다.");
            _targets = targets.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
            if (_targets.Length == 0) throw new InvalidOperationException("Graphics Set1의 표시 항목이 없습니다.");
        }

        public Forms.CheckState State
        {
            get
            {
                var selected = Paths();
                int count = _targets.Count(selected.Contains);
                return count == 0 ? Forms.CheckState.Unchecked : count == _targets.Length
                    ? Forms.CheckState.Checked : Forms.CheckState.Indeterminate;
            }
        }

        private List<string> Paths()
        {
            return ((_path.GetValue(_view, null) as string) ?? "").Split(';')
                .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }

        public void Set(bool enabled)
        {
            var paths = Paths();
            if (enabled) { foreach (string path in _targets) if (!paths.Contains(path)) paths.Add(path); }
            else paths.RemoveAll(p => _targets.Contains(p));
            string next = string.Join(";", paths);
            WritePaths(next);
        }

        private void WritePaths(string next)
        {
            if (next == (_path.GetValue(_view, null) as string)) return;
            // Set the public dependency-property source first. Its SDK callback removes OLD
            // output subscriptions, then assigns the new paths and registers NEW subscriptions.
            // ShapeObjectPath's wrapper assigns the new backing field before removal in 4.4.50.
            // This runtime display choice is deliberately not written to recipe XAML or saved.
            _source.SetValue(_view, next, null);
            if ((_path.GetValue(_view, null) as string) != next)
                throw new InvalidOperationException("그래픽 선택 상태가 반영되지 않았습니다.");
        }

        public Task ReconnectAsync()
        {
            var element = _view as DispatcherObject;
            if (element == null) throw new InvalidOperationException("그래픽 화면 Dispatcher를 찾지 못했습니다.");
            // SDK RemoveShapeList/UpdateShapeList enqueue Background operations. A barrier
            // at the same priority waits for each transition, without blocking the UI thread.
            return ReconnectAsync(() => element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
        }

        internal async Task ReconnectAsync(Func<Task> drain)
        {
            string original = (_path.GetValue(_view, null) as string) ?? "";
            try
            {
                Set(State == Forms.CheckState.Unchecked);
                await drain();
            }
            finally
            {
                // Preserve partial selections and an intentionally unchecked module as well.
                WritePaths(original);
                await drain();
            }
        }

        public static FrontendGraphics Bind(Forms.Control frontend)
        {
            var matches = new List<FrontendGraphics>();
            foreach (var host in Children(frontend).OfType<ElementHost>())
            {
                if (host.Child == null) continue;
                foreach (var view in Visuals(host.Child).OfType<FrameworkElement>())
                {
                    if (view.GetType().FullName != "FrontendUI.Design.Controls.ImageControl") continue;
                    var tree = Get(view, "ShapeSourceList") as IEnumerable;
                    var targets = new List<string>();
                    foreach (object leaf in Leaves(tree))
                    {
                        object target = Get(leaf, "HolderTarget");
                        object owner = target == null ? null : Get(target, "Owner");
                        if (owner == null || !string.Equals(Get(owner, "Name") as string, "Graphics Set1", StringComparison.Ordinal)) continue;
                        var fullPath = target.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IVmFullPath");
                        if (fullPath == null) continue;
                        string path = fullPath.GetProperty("FullPath").GetValue(target, null) as string;
                        var baseType = view.GetType().Assembly.GetType("FrontendUI.Design.Controls.BaseControl", true);
                        var convert = baseType.GetMethod("CheckBindingPathWithOutSolName", BindingFlags.Static | BindingFlags.NonPublic);
                        if (convert == null) throw new InvalidOperationException("SDK 그래픽 경로 변환을 찾지 못했습니다.");
                        targets.Add((string)convert.Invoke(null, new[] { (object)path, Get(view, "ModuleSource") }));
                    }
                    if (targets.Count > 0) matches.Add(new FrontendGraphics(view, targets));
                }
            }
            if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
                ? "현재 영상에서 Graphics Set1을 찾지 못했습니다."
                : "Graphics Set1이 연결된 영상이 여러 개라 선택할 수 없습니다.");
            return matches[0];
        }

        private static object Get(object value, string name)
        {
            var property = value.GetType().GetProperty(name);
            if (property == null) throw new InvalidOperationException("SDK 속성 없음: " + name);
            return property.GetValue(value, null);
        }
        private static IEnumerable<object> Leaves(IEnumerable tree)
        {
            if (tree == null) yield break;
            foreach (object node in tree)
            {
                var children = ((IEnumerable)Get(node, "EnumEntrysA")).Cast<object>().ToArray();
                if (children.Length == 0) yield return node;
                else foreach (object leaf in Leaves(children)) yield return leaf;
            }
        }
        private static IEnumerable<Forms.Control> Children(Forms.Control root)
        { yield return root; foreach (Forms.Control child in root.Controls) foreach (var item in Children(child)) yield return item; }
        private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item; }
    }
}
