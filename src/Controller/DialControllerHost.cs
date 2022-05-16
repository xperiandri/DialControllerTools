using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

using Community.VisualStudio.Toolkit;

using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.PlatformUI.Shell;
using Microsoft.VisualStudio.PlatformUI.Shell.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using Windows.UI.Input;

using Task = System.Threading.Tasks.Task;

namespace DialControllerTools
{
    internal class DialControllerHost : IDialControllerHost
    {
        private static readonly Dictionary<IntPtr, RadialController> controllersMapping = new Dictionary<IntPtr, RadialController>();

        private readonly IAsyncServiceProvider serviceProvider;
        private readonly OutputWindowPane outputPane;

        private RadialControllerStatusBarHost _status;
        private readonly ImmutableArray<IDialController> _controllers;
        private readonly List<IDialController> _enabledControllers;
        private IDialController currentController;
        private bool allowRelease;
        private bool wasUsed;

        public static DialControllerHost Instance
        {
            get;
            private set;
        }

        public IDialController CurrentController
        {
            get => currentController;
            set
            {
                currentController = value;
                _status.Text = currentController.Moniker;
            }
        }

        private DialControllerHost(IAsyncServiceProvider serviceProvider, OutputWindowPane outputPane, in ImmutableArray<IDialController> controllers)
        {
            this.serviceProvider = serviceProvider;
            this.outputPane = outputPane;
            this._controllers = controllers;
            this._enabledControllers = new List<IDialController>(controllers.Length);

            FloatingWindowManager.FloatingWindowCreated += (sender, args) => CreateControllerForWindow(args.Window);
            //var floatingWindowManager = ViewManager.Instance.FloatingWindowManager;
            //floatingWindowManager.

            void OnMainWindowLoaded(object sender, EventArgs args)
            {
                CreateStatusBarItem();
                UpdateEnabledControllers(DialPackage.Options);
            }

            ExecuteWhenWindowIsLoaded(Application.Current.MainWindow, OnMainWindowLoaded);
        }

        public static async Task InitializeAsync(IAsyncServiceProvider provider, JoinableTask optionsLoadTask, CancellationToken cancellationToken)
        {
            var compositionServiceTask = provider.GetServiceAsync<SComponentModel, IComponentModel>(cancellationToken);
            var outputPaneTask = DialPackage.GetOutputPaneAsync();
            var controllersTask = provider.GetControllersAsync(cancellationToken);
            await Task.WhenAll(compositionServiceTask, outputPaneTask, controllersTask, optionsLoadTask.Task);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
#pragma warning disable VSTHRD103 // Call async methods when in an async method
            Instance = new DialControllerHost(provider, outputPaneTask.Result, controllersTask.Result);
#pragma warning restore VSTHRD103 // Call async methods when in an async method

            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow || window is FloatingWindow)
                    Instance.CreateControllerForWindow(window);
            }
        }

        private static void ExecuteWhenWindowIsLoaded(Window window, RoutedEventHandler action)
        {
            if (window.IsLoaded) action(window, new RoutedEventArgs());
            else window.Loaded += action;
        }

        private void CreateStatusBarItem()
        {
            _status = new RadialControllerStatusBarHost { Name = "PART_DialControllerHost" };
            var injector = new StatusBarInjector(Application.Current.MainWindow);
            injector.InjectControl(_status);
        }

        private void CreateControllerForWindow(Window window)
        {
            void CreateController(object sender, EventArgs args)
            {
                window.Loaded -= CreateController;
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                Instance.CreateController(hwnd);
                SetDefaultItems(hwnd);

                if (window.IsActive) Instance.ApplyCurrentControllerStateForWindow((Window)sender);
                void OnWindowActivated(object sender, EventArgs args)
                {
                    Instance.ApplyCurrentControllerStateForWindow((Window)sender);
                }
                window.Activated += OnWindowActivated;

                void RemoveController(object sender, RoutedEventArgs args)
                {
                    window.Activated -= OnWindowActivated;
                    window.Unloaded -= RemoveController;
                    if (controllersMapping.TryGetValue(hwnd, out var controller))
                    {
                        UnsubscribeFromController(controller);
                        controllersMapping.Remove(hwnd);
                    }
                }
                window.Unloaded += RemoveController;
            }

            ExecuteWhenWindowIsLoaded(window, CreateController);
        }

        private void CreateController(IntPtr hwnd)
        {
            var interop = (IRadialControllerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(RadialController));
            Guid guid = typeof(RadialController).GetInterface("IRadialController").GUID;

            var radialController = interop.CreateForWindow(hwnd, ref guid);
            if (radialController is null)
            {
                outputPane.WriteLine("Couldn't create RadialController");
                return;
            }

            foreach (IDialController controller in _enabledControllers)
            {
                radialController.Menu.Items.Add(controller.MenuItem);
            }

            radialController.RotationChanged += OnRotationChanged;
            radialController.ButtonClicked += OnButtonClicked;
            radialController.ControlAcquired += OnControlAcquired;
            //radialController.ControlLost += OnControlLost;
            controllersMapping.Add(hwnd, radialController);
        }

        internal void UnsubscribeFromController(RadialController radialController)
        {
            radialController.RotationChanged -= OnRotationChanged;
            radialController.ButtonClicked -= OnButtonClicked;
            radialController.ControlAcquired -= OnControlAcquired;
            //radialController.ControlLost -= OnControlLost;
        }

        private static void SetDefaultItems(IntPtr hwnd)
        {
            RadialControllerConfiguration config;
            var radialControllerConfigInterop = (IRadialControllerConfigurationInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(RadialControllerConfiguration));
            Guid guid = typeof(RadialControllerConfiguration).GetInterface("IRadialControllerConfiguration").GUID;

            config = radialControllerConfigInterop.GetForWindow(hwnd, ref guid);
            config.SetDefaultMenuItems(new RadialControllerSystemMenuItemKind[0]);
        }

        private bool UpdateEnabledControllers(Options options)
        {
            var newControllers = _controllers.Where(c => options.MenuVisibility[c.Moniker]).ToImmutableArray();
            var anyChanges = !newControllers.SequenceEqual(_enabledControllers);
            if (anyChanges)
            {
                _enabledControllers.Clear();
                _enabledControllers.AddRange(newControllers);
            }

            var defaultMenu = options.DefaultProvider.ToString();
            if (!options.MenuVisibility[defaultMenu])
            {
                defaultMenu = options.MenuVisibility.FirstOrDefault(_ => _.Value).Key ?? nameof(KnownProviders.Scroll);
            }
            CurrentController = _enabledControllers.First(c => c.Moniker == defaultMenu);

            return anyChanges;
        }

        internal void OptionsApplied(object sender, EventArgs e)
        {
            var options = (Options)sender;
            var anyControllerChangesMade = UpdateEnabledControllers(options);

            if (!anyControllerChangesMade) return;

            foreach (var controller in controllersMapping.Values)
            {
                var menuItems = controller.Menu.Items;
                for (int i = 0; i < _enabledControllers.Count; i++)
                {
                    var newMenuItem = _enabledControllers[i].MenuItem;
                    if (i < menuItems.Count)
                    {
                        if (menuItems[i] != _enabledControllers[i].MenuItem)
                        {
                            menuItems[i] = newMenuItem;
                        }
                    }
                    else
                    {
                        menuItems.Add(newMenuItem);
                    }
                }
            }
        }

        public void ApplyCurrentControllerStateForWindow(Window window)
        {
            var radialController = controllersMapping[new WindowInteropHelper(window).Handle];
            ApplyCurrentControllerState(radialController);
        }

        private void ApplyCurrentControllerState(RadialController radialController)
        {
            var menuItem = CurrentController.MenuItem;
            if (radialController.Menu.GetSelectedMenuItem() != menuItem)
            {
                radialController.Menu.SelectMenuItem(menuItem);
            }
            radialController.UseAutomaticHapticFeedback = CurrentController.IsHapticFeedbackEnabled;
        }

        public void RequestActivation(IDialController controller)
        {
            var activeWindow = Application.Current.Windows.OfType<Window>().SingleOrDefault(w => w.IsActive);
            if (activeWindow is not null)
            {
                allowRelease = true;
                wasUsed = false;
                CurrentController = controller;
                ApplyCurrentControllerStateForWindow(activeWindow);
            }
        }

        public void ReleaseActivation()
        {
            if (wasUsed || !allowRelease)
            {
                wasUsed = false;
                return;
            }

            allowRelease = false;
            var activeWindow = Application.Current.Windows.OfType<Window>().SingleOrDefault(w => w.IsActive);
            if (activeWindow is not null)
            {
                var radialController = controllersMapping[new WindowInteropHelper(activeWindow).Handle];
                if (radialController.Menu.TrySelectPreviouslySelectedMenuItem())
                {
                    MenuItemSelected(radialController);
                }
            }
        }

        private void OnControlAcquired(RadialController sender, RadialControllerControlAcquiredEventArgs args)
        {
            //_status.IsActive = true;
            MenuItemSelected(sender);
        }

        private void MenuItemSelected(RadialController radualController)
        {
            RadialControllerMenuItem selectedItem = radualController.Menu.GetSelectedMenuItem();
            if (selectedItem?.DisplayText != CurrentController.MenuItem.DisplayText)
            {
                allowRelease = false;
                // null when window is closed
                var controller = _enabledControllers.FirstOrDefault(c => c.MenuItem == selectedItem);
                if (controller is not null)
                {
                    CurrentController = controller;
                    radualController.UseAutomaticHapticFeedback = CurrentController.IsHapticFeedbackEnabled;
                }

            }
        }

        //private void OnControlLost(RadialController sender, object args) => _status.IsActive = false;

        private async void OnButtonClicked(RadialController sender, RadialControllerButtonClickedEventArgs args)
        {
            if (!await VS.Solutions.IsOpenAsync()) //assume most controllers require a solution
                return;

            wasUsed = true;
            IEnumerable<IDialController> controllers = GetApplicableControllers(sender).Where(c => c.CanHandleClick);
            outputPane.WriteLine("Click: " + string.Join(", ", controllers.Select(c => c.Moniker)));

            foreach (IDialController controller in controllers)
            {
                try
                {
                    bool handled = controller.OnClick();

                    if (handled)
                        break;
                }
                catch (Exception ex)
                {
                    outputPane.WriteLine($"Controller {controller.Moniker} failed:");
                    outputPane.WriteLine(ex.ToString());
                }
            }
        }

        private async void OnRotationChanged(RadialController sender, RadialControllerRotationChangedEventArgs args)
        {
            if (!await VS.Solutions.IsOpenAsync()) //assume most controllers require a solution
                return;

            wasUsed = true;
            IEnumerable<IDialController> controllers = GetApplicableControllers(sender).Where(c => c.CanHandleRotate);
            RotationDirection direction = args.RotationDeltaInDegrees > 0 ? RotationDirection.Right : RotationDirection.Left;

            outputPane.WriteLine("Rotate: " + string.Join(", ", controllers.Select(c => c.Moniker)));

            foreach (IDialController controller in controllers)
            {
                try
                {
                    bool handled = controller.OnRotate(direction);

                    if (handled)
                        break;
                }
                catch (Exception ex)
                {
                    outputPane.WriteLine($"Controller {controller.Moniker} failed:");
                    outputPane.WriteLine(ex.ToString());
                }
            }
        }

        private IEnumerable<IDialController> GetApplicableControllers(RadialController controller)
        {
            string moniker = controller.Menu.GetSelectedMenuItem()?.DisplayText;

            if (string.IsNullOrEmpty(moniker))
                Enumerable.Empty<IDialController>();

            try
            {
                return _controllers.Where(c => c.Moniker == moniker);
            }
            catch (Exception ex)
            {
                outputPane.WriteLine($"Cannot retrieve controllers for {moniker}:");
                outputPane.WriteLine(ex.ToString());
                return Enumerable.Empty<IDialController>();
            }
        }
    }
}
