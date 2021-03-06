﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GestureSign.Common.Applications;
using GestureSign.Common.Input;
using GestureSign.Common.Log;
using ManagedWinapi.Windows;

namespace GestureSign.Common.Plugins
{
    public class PluginManager : IPluginManager
    {
        #region Private Variables

        // Create variable to hold the only allowed instance of this class
        static readonly PluginManager _Instance = new PluginManager();
        List<IPluginInfo> _Plugins = new List<IPluginInfo>();
        private Task _lastActionTask;
        private SynchronizationContext _mainContext;

        #endregion

        #region Public Properties

        public IPluginInfo[] Plugins { get { return _Plugins.ToArray(); } }

        public static PluginManager Instance
        {
            get { return _Instance; }
        }

        #endregion

        #region Constructors

        protected PluginManager()
        {

        }

        #endregion

        #region Events

        protected void PointCapture_GestureRecognized(object sender, RecognitionEventArgs e)
        {
            var pointCapture = (IPointCapture)sender;
            // Get action to be executed
            var executableActions = ApplicationManager.Instance.GetRecognizedDefinedAction(e.GestureName)?.ToList();
            if (executableActions == null) return;
            ExecuteAction(executableActions, pointCapture.Mode, e.ContactIdentifiers, e.FirstCapturedPoints, e.Points);
        }

        #endregion

        #region Public Methods

        public void ExecuteAction(List<IAction> executableActions, CaptureMode mode, List<int> contactIdentifiers, List<Point> firstCapturedPoints, List<List<Point>> points)
        {
            // Exit if we're teaching
            if (mode == CaptureMode.Training)
                return;
            var pointInfo = new PointInfo(firstCapturedPoints, points, _mainContext);
            var action = new Action<object>(o =>
            {
                foreach (IAction executableAction in executableActions)
                {
                    // Exit if there is no action configured
                    if (executableAction?.Commands == null || !Compute(executableAction.Condition, points, contactIdentifiers))
                        continue;

                    var commandList = executableAction.Commands.Where(command => command != null && command.IsEnabled).ToList();
                    foreach (var command in commandList)
                    {
                        if (mode == CaptureMode.UserDisabled && !"GestureSign.CorePlugins.ToggleDisableGestures".Equals(command.PluginClass))
                            continue;

                        if (!WaitForInputIdle(pointInfo.Window, 1000))
                            break;

                        // Locate the plugin associated with this action
                        IPluginInfo pluginInfo = FindPluginByClassAndFilename(command.PluginClass, command.PluginFilename);

                        // Exit if there is no plugin available for action
                        if (pluginInfo == null)
                            continue;

                        if (commandList.IndexOf(command) == 0)
                        {
                            if (executableAction.ActivateWindow == null && pluginInfo.Plugin.ActivateWindowDefault ||
                            executableAction.ActivateWindow.HasValue && executableAction.ActivateWindow.Value)
                                if (pointInfo.Window?.HWnd.ToInt64() != SystemWindow.ForegroundWindow?.HWnd.ToInt64())
                                    SystemWindow.ForegroundWindow = pointInfo.Window;
                        }

                        // Load action settings into plugin
                        pluginInfo.Plugin.Deserialize(command.CommandSettings);
                        // Execute plugin process
                        pluginInfo.Plugin.Gestured(pointInfo);
                    }
                }
            });

            var observeExceptions = new Action<Task>(t =>
            {
                Logging.LogException(t.Exception.InnerException);
            });

            if (_lastActionTask == null)
            {
                _lastActionTask = Task.Factory.StartNew(action, null);
                _lastActionTask.ContinueWith(observeExceptions, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                _lastActionTask = _lastActionTask.ContinueWith(action);
                _lastActionTask.ContinueWith(observeExceptions, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        public bool LoadPlugins(IHostControl host)
        {
            // Default return value to failure
            bool bFailed = true;

            // Clear any existing plugins
            _Plugins = new List<IPluginInfo>();
            //_Plugins.Clear();
            string directoryPath = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
            if (directoryPath == null) return true;

            // Load core plugins.
            string corePluginsPath = Path.Combine(directoryPath, "GestureSign.CorePlugins.dll");
            if (File.Exists(corePluginsPath))
            {
                _Plugins.AddRange(LoadPluginsFromAssembly(corePluginsPath, host));
                bFailed = false;
            }

            var extraPluginsPath = Path.Combine(directoryPath, "Plugins");
            if (Directory.Exists(extraPluginsPath))
            {
                // Load extra plugins.
                foreach (string sFilePath in Directory.GetFiles(extraPluginsPath, "*.dll"))
                {
                    _Plugins.AddRange(LoadPluginsFromAssembly(sFilePath, host));
                    bFailed = false;
                }
            }


            return bFailed;
        }

        public IPluginInfo FindPluginByClassAndFilename(string PluginClass, string PluginFilename)
        {
            // Get reference to plugin using PluginClass and PluginFilename
            return _Plugins.FirstOrDefault(p => p.Class == PluginClass && p.Filename == PluginFilename);
        }

        public bool PluginExists(string PluginClass, string PluginFilename)
        {
            return _Plugins.Exists(p => p.Class == PluginClass && p.Filename == PluginFilename);
        }

        #endregion

        #region Private Methods

        private List<IPluginInfo> LoadPluginsFromAssembly(string assemblyLocation, IHostControl hostControl)
        {
            List<IPluginInfo> retPlugins = new List<IPluginInfo>();

            //To avoid exception System.NotSupportedException
            byte[] file = File.ReadAllBytes(assemblyLocation);
            Assembly aPlugin = Assembly.Load(file);

            Type[] tPluginTypes = aPlugin.GetTypes();

            foreach (Type tPluginType in tPluginTypes)
                if (tPluginType.GetInterface("IPlugin") != null)
                {
                    IPlugin plugin = Activator.CreateInstance(tPluginType) as IPlugin;

                    // If we have a new instance of a plugin, initialize it and add it to return list
                    if (plugin != null)
                    {
                        plugin.HostControl = hostControl;
                        plugin.Initialize();
                        retPlugins.Add(new PluginInfo(plugin, tPluginType.FullName, Path.GetFileName(assemblyLocation)));
                    }
                }

            return retPlugins;
        }

        private bool Compute(string condition, List<List<Point>> pointList, List<int> contactIdentifiers)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;

            string expression = GetExpression(condition, pointList, contactIdentifiers);
            try
            {
                DataTable dataTable = new DataTable();
                var result = dataTable.Compute(expression, null);
                return result is DBNull || Convert.ToBoolean(result);
            }
            catch (EvaluateException)
            {
                return false;
            }
        }

        private string GetExpression(string condition, List<List<Point>> pointList, List<int> contactIdentifiers)
        {
            StringBuilder sb = new StringBuilder(condition);

            for (int i = 1; i <= pointList.Count; i++)
            {
                var format = "finger_{0}_start_X";
                string variable = string.Format(format, i);
                sb.Replace(variable, pointList[i - 1].FirstOrDefault().X.ToString());

                format = "finger_{0}_start_Y";
                variable = string.Format(format, i);
                sb.Replace(variable, pointList[i - 1].FirstOrDefault().Y.ToString());

                format = "finger_{0}_end_X";
                variable = string.Format(format, i);
                sb.Replace(variable, pointList[i - 1].LastOrDefault().X.ToString());

                format = "finger_{0}_end_Y";
                variable = string.Format(format, i);
                sb.Replace(variable, pointList[i - 1].LastOrDefault().Y.ToString());

                format = "finger_{0}_ID";
                variable = string.Format(format, i);
                sb.Replace(variable, contactIdentifiers[i - 1].ToString());
            }
            return sb.ToString();
        }

        private static bool WaitForInputIdle(SystemWindow targetWindow, int timeout = 0)
        {
            try
            {
                var windowProcess = targetWindow.Process;
                return windowProcess.WaitForInputIdle(timeout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region ILoadable Methods

        public void Load(IHostControl host, SynchronizationContext syncContext = null)
        {
            _mainContext = syncContext;
            // Create empty list of plugins, then load as many as possible from plugin directory
            LoadPlugins(host);

            if (host == null) return;
            host.PointCapture.GestureRecognized += PointCapture_GestureRecognized;
        }

        #endregion
    }
}
