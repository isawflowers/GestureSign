﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GestureSign.Common.Configuration;
using GestureSign.Common.Gestures;
using GestureSign.Common.Input;
using GestureSign.Common.InterProcessCommunication;
using ManagedWinapi.Windows;

namespace GestureSign.Common.Applications
{
    public class ApplicationManager : IApplicationManager
    {
        #region Private Variables

        // Create variable to hold the only allowed instance of this class
        private static ApplicationManager _instance;
        private List<IApplication> _Applications;
        IApplication _CurrentApplication = null;
        IEnumerable<IApplication> RecognizedApplication;
        private Timer timer;
        public static event EventHandler OnLoadApplicationsCompleted;
        #endregion

        #region Public Instance Properties

        public SystemWindow CaptureWindow { get; private set; }
        public IApplication CurrentApplication
        {
            get { return _CurrentApplication; }
            set
            {
                _CurrentApplication = value;
            }
        }

        public List<IApplication> Applications
        {
            get
            {
                return _Applications ?? new List<IApplication>();
            }
        }

        public static ApplicationManager Instance
        {
            get { return _instance ?? (_instance = new ApplicationManager()); }
        }

        public static bool FinishedLoading { get; set; }

        #endregion

        #region Constructors

        protected ApplicationManager()
        {
            Action<bool> loadCompleted =
                result =>
                {
                    if (!result)
                        if (!LoadBackup())
                            if (!LoadDefaults())
                                _Applications = new List<IApplication>();
                    if (OnLoadApplicationsCompleted != null) OnLoadApplicationsCompleted(this, EventArgs.Empty);
                    FinishedLoading = true;
                };
            // Load applications from disk, if file couldn't be loaded, create an empty applications list
            LoadApplications().ContinueWith(antecendent => loadCompleted(antecendent.Result));
        }



        #endregion

        #region Events

        protected void PointCapture_CaptureStarted(object sender, PointsCapturedEventArgs e)
        {
            var pointCapture = (IPointCapture)sender;
            if (pointCapture.Mode == CaptureMode.Training) return;

            if (Environment.OSVersion.Version.Major == 6)
            {
                IntPtr hwndCharmBar = FindWindow("NativeHWNDHost", "Charm Bar");
                var window = SystemWindow.FromPointEx(SystemWindow.DesktopWindow.Rectangle.Right - 1, 1, true, true);

                if (window != null && window.HWnd.Equals(hwndCharmBar))
                {
                    e.Cancel = false;
                    e.BlockTouchInputThreshold = 0;
                    return;
                }
            }

            CaptureWindow = GetWindowFromPoint(e.FirstCapturedPoints.FirstOrDefault());
            RecognizedApplication = GetApplicationFromWindow(CaptureWindow);

            int maxThreshold = 0;
            bool? limitNumberFlag = null;

            foreach (IApplication app in RecognizedApplication)
            {
                UserApplication userApplication = app as UserApplication;
                if (userApplication != null)
                {
                    maxThreshold = userApplication.BlockTouchInputThreshold > maxThreshold ? userApplication.BlockTouchInputThreshold : maxThreshold;

                    //Got UserApplication
                    if (limitNumberFlag == null)
                        limitNumberFlag = e.Points.Count < userApplication.LimitNumberOfFingers;
                    else
                        limitNumberFlag |= e.Points.Count < userApplication.LimitNumberOfFingers;
                }
                else
                {
                    IgnoredApplication ignoredApplication = app as IgnoredApplication;
                    if (ignoredApplication != null && ignoredApplication.IsEnabled)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
            e.Cancel = pointCapture.SourceDevice == Device.Touch && (limitNumberFlag ?? e.Points.Count == 1);
            e.BlockTouchInputThreshold = maxThreshold;
        }

        protected void PointCapture_BeforePointsCaptured(object sender, PointsCapturedEventArgs e)
        {
            // Derive capture window from capture point
            CaptureWindow = GetWindowFromPoint(e.FirstCapturedPoints.FirstOrDefault());
            RecognizedApplication = GetApplicationFromWindow(CaptureWindow);
        }

        #endregion

        #region Custom Events

        public static event ApplicationChangedEventHandler ApplicationChanged;

        #endregion

        #region Public Methods

        public void Load(IPointCapture pointCapture)
        {
            // Shortcut method to control singleton instantiation
            // Consume Point Capture events
            if (pointCapture != null)
            {
                pointCapture.CaptureStarted += new PointsCapturedEventHandler(PointCapture_CaptureStarted);
                pointCapture.BeforePointsCaptured += new PointsCapturedEventHandler(PointCapture_BeforePointsCaptured);
            }
        }

        public void AddApplication(IApplication application)
        {
            _Applications.Add(application);
            ApplicationChanged?.Invoke(this, new ApplicationChangedEventArgs(application));
        }

        public void AddApplicationRange(List<IApplication> applications)
        {
            _Applications.AddRange(applications);
            ApplicationChanged?.Invoke(this, new ApplicationChangedEventArgs(applications.FirstOrDefault()));
        }

        public void RemoveApplication(IApplication application)
        {
            _Applications.Remove(application);
            ApplicationChanged?.Invoke(this, new ApplicationChangedEventArgs(application));
        }

        public void ReplaceApplication(IApplication oldApplication, IApplication newApplication)
        {
            _Applications.Remove(oldApplication);
            _Applications.Add(newApplication);
            ApplicationChanged?.Invoke(this, new ApplicationChangedEventArgs(newApplication));
        }

        public void RemoveIgnoredApplications(string applicationName)
        {
            _Applications.RemoveAll(app => app is IgnoredApplication && app.Name == applicationName);
        }

        public void RenameGesture(string newName, string oldName)
        {
            _Applications.ForEach(app => app.Actions?.ForEach(action =>
              {
                  if (action.GestureName == oldName)
                      action.GestureName = newName;
              }));

            SaveApplications();
        }

        public void TrimGesture()
        {
            _Applications.ForEach(app => app?.Actions?.ForEach(a =>
            {
                if (!GestureManager.Instance.GestureExists(a.GestureName))
                    a.GestureName = string.Empty;
            }));
            SaveApplications();
        }

        public bool SaveApplications()
        {
            if (timer == null)
            {
                timer = new Timer(new TimerCallback(SaveFile), true, 200, Timeout.Infinite);
            }
            else timer.Change(200, Timeout.Infinite);
            return true;
        }

        private void SaveFile(object state)
        {
            bool notice = (bool)state;
            // Save application list
            bool flag = FileManager.SaveObject(
                 _Applications, Path.Combine(AppConfig.ApplicationDataPath, "Actions.act"), true);
            if (flag && notice) { NamedPipe.SendMessageAsync("LoadApplications", "GestureSignDaemon"); }

        }

        public Task<bool> LoadApplications()
        {
            return Task.Run(() =>
            {
                // Load application list from file
                _Applications =
                    FileManager.LoadObject<List<IApplication>>(
                        Path.Combine(AppConfig.ApplicationDataPath, "Actions.act"), true, true);
                return _Applications != null;
            });
        }

        private bool LoadDefaults()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Defaults\Actions.act");

            _Applications = FileManager.LoadObject<List<IApplication>>(path, true, true);
            // Ensure we got an object back
            if (_Applications == null)
                return false; // No object, failed

            return true; // Success
        }

        private bool LoadBackup()
        {
            var actionfiles = Directory.GetFiles(AppConfig.ApplicationDataPath, "Actions*.act");
            foreach (var file in actionfiles)
            {
                _Applications = FileManager.LoadObject<List<IApplication>>(file, true, true);
                if (_Applications != null) return true;
            }
            return false;
        }

        public SystemWindow GetWindowFromPoint(Point point)
        {
            return SystemWindow.FromPointEx(point.X, point.Y, true, true);
        }

        public IApplication[] GetApplicationFromWindow(SystemWindow Window, bool userApplicationOnly = false)
        {
            if (Applications == null)
            {
                return new[] { GetGlobalApplication() };
            }
            IApplication[] definedApplications = userApplicationOnly
                ? FindMatchApplications(Applications.Where(a => a is UserApplication), Window)
                : FindMatchApplications(Applications.Where(a => !(a is GlobalApplication)), Window);
            // Try to find any user or ignored applications that match the given system window
            // If not user or ignored application could be found, return the global application
            return definedApplications.Length != 0
                ? definedApplications
                : userApplicationOnly ? null : new IApplication[] { GetGlobalApplication() };
        }

        public IEnumerable<IApplication> GetApplicationFromPoint(Point testPoint)
        {
            var systemWindow = GetWindowFromPoint(testPoint);
            return GetApplicationFromWindow(systemWindow);
        }

        public IEnumerable<IAction> GetRecognizedDefinedAction(string GestureName)
        {
            return GetEnabledDefinedAction(GestureName, RecognizedApplication, true);
        }

        public IAction GetAnyDefinedAction(string actionName, string applicationName)
        {
            IApplication app = GetGlobalApplication().Name == applicationName ? GetGlobalApplication() : GetExistingUserApplication(applicationName);
            if (app != null && app.Actions.Exists(a => a.Name == actionName))
                return app.Actions.Find(a => a.Name == actionName);

            return null;
        }

        public IEnumerable<IAction> GetEnabledDefinedAction(string gestureName, IEnumerable<IApplication> application, bool useGlobal)
        {
            if (application == null)
            {
                return null;
            }
            // Attempt to retrieve an action on the application passed in
            IEnumerable<IAction> finalAction =
                application.Where(app => !(app is IgnoredApplication)).SelectMany(app => app.Actions.Where(a => a.IsEnabled && a.GestureName.Equals(gestureName, StringComparison.Ordinal)));
            // If there is was no action found on given application, try to get an action for global application
            if (!finalAction.Any() && useGlobal)
                finalAction = GetGlobalApplication().Actions.Where(a => a.GestureName == gestureName);

            // Return whatever the result was
            return finalAction;
        }

        public IApplication GetExistingUserApplication(string ApplicationName)
        {
            return Applications.FirstOrDefault(a => a is UserApplication && a.Name == ApplicationName.Trim());
        }

        public bool IsGlobalAction(string ActionName)
        {
            return _Applications.Exists(a => a is GlobalApplication && a.Actions.Any(ac => ac.Name == ActionName.Trim()));
        }

        public bool ApplicationExists(string ApplicationName)
        {
            return _Applications.Exists(a => a.Name == ApplicationName.Trim());
        }

        public IApplication[] GetAvailableUserApplications()
        {
            return Applications.Where(a => a is UserApplication).OrderBy(a => a.Name).Cast<UserApplication>().ToArray();
        }

        public IEnumerable<IgnoredApplication> GetIgnoredApplications()
        {
            return Applications.Where(a => a is IgnoredApplication).OrderBy(a => a.Name).Cast<IgnoredApplication>();
        }

        public IApplication GetGlobalApplication()
        {
            if (_Applications == null)
            {
                _Applications = new List<IApplication> { new GlobalApplication { Group = String.Empty } };
            }
            else if (!_Applications.Exists(a => a is GlobalApplication))
                _Applications.Add(new GlobalApplication() { Group = String.Empty });

            return _Applications.FirstOrDefault(a => a is GlobalApplication);
        }

        public IEnumerable<IApplication> GetAllGlobalApplication()
        {
            if (!_Applications.Exists(a => a is GlobalApplication))
                _Applications.Add(new GlobalApplication() { Group = String.Empty });
            return _Applications.Where(a => a is GlobalApplication);
        }
        public void RemoveGlobalAction(string ActionName)
        {
            RemoveAction(ActionName, true);
        }

        public void RemoveNonGlobalAction(string ActionName)
        {
            RemoveAction(ActionName, false);
        }

        public IApplication[] FindMatchApplications<TApplication>(MatchUsing matchUsing, string matchString, string excludedApplication = null) where TApplication : IApplication
        {
            return _Applications.FindAll(
                    a => a is TApplication &&
                        matchString.Equals(a.MatchString, StringComparison.CurrentCultureIgnoreCase) &&
                        matchUsing == a.MatchUsing &&
                        excludedApplication != a.Name).ToArray();
        }

        public SystemWindow GetForegroundApplications()
        {
            CaptureWindow = SystemWindow.ForegroundWindow;
            RecognizedApplication = GetApplicationFromWindow(CaptureWindow);
            return CaptureWindow;
        }

        public static string GetNextActionName(string name, IApplication application, int number = 1)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));

            var actionName = number == 1 ? name : $"{name}({number})";
            if (application.Actions.Exists(a => a.Name == actionName))
                return GetNextActionName(name, application, ++number);
            return actionName;
        }

        #endregion

        #region Private Methods

        private void RemoveAction(string ActionName, bool Global)
        {
            if (Global)
                // Attempt to remove action from global actions
                GetGlobalApplication().RemoveAllActions(a => a.Name.Trim() == ActionName.Trim());
            else
                // Select applications where this action may exist and delete them
                foreach (IApplication app in GetAvailableUserApplications().Where(a => a.Actions.Any(ac => ac.Name == ActionName)))
                    app.RemoveAllActions(a => a.Name.Trim() == ActionName.Trim());
        }

        private IApplication[] FindMatchApplications(IEnumerable<IApplication> applications, SystemWindow window)
        {
            var byFileName = new List<IApplication>();
            var byTitle = new List<IApplication>();
            var byClass = new List<IApplication>();
            foreach (var app in applications)
            {
                switch (app.MatchUsing)
                {
                    case MatchUsing.WindowClass:
                        byClass.Add(app);
                        break;
                    case MatchUsing.WindowTitle:
                        byTitle.Add(app);
                        break;
                    case MatchUsing.ExecutableFilename:
                        byFileName.Add(app);
                        break;
                    case MatchUsing.All:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            List<IApplication> result = new List<IApplication>();
            string windowMatchString;
            if (byClass.Count != 0)
            {
                try
                {
                    windowMatchString = window.ClassName;
                    result.AddRange(byClass.Where(a => a.MatchString != null && CompareString(a.MatchString, windowMatchString, a.IsRegEx)));
                }
                catch
                {
                    // ignored
                }
            }
            if (byTitle.Count != 0)
            {
                try
                {
                    windowMatchString = window.Title;
                    result.AddRange(byTitle.Where(a => a.MatchString != null && CompareString(a.MatchString, windowMatchString, a.IsRegEx)));
                }
                catch
                {
                    // ignored
                }
            }
            if (byFileName.Count != 0)
            {
                try
                {
                    windowMatchString = ((Func<string>)delegate
                    {
                        if (Environment.OSVersion.Version.Major >= 10 && "ApplicationFrameWindow".Equals(window.ClassName))
                        {
                            var realWindow = window.AllChildWindows.FirstOrDefault(w => "Windows.UI.Core.CoreWindow".Equals(w.ClassName));
                            if (realWindow != null)
                                return realWindow.Process.MainModule.ModuleName;
                        }
                        return window.Process.MainModule.ModuleName;
                    }).Invoke();

                    result.AddRange(byFileName.Where(a => a.MatchString != null && CompareString(a.MatchString, windowMatchString, a.IsRegEx)));
                }
                catch
                {
                    // ignored
                }
            }
            return result.ToArray();
        }

        private static bool CompareString(string compareMatchString, string windowMatchString, bool useRegEx)
        {
            if (string.IsNullOrEmpty(windowMatchString)) return false;
            return useRegEx
                ? Regex.IsMatch(windowMatchString, compareMatchString, RegexOptions.Singleline | RegexOptions.IgnoreCase)
                : string.Equals(windowMatchString.Trim(), compareMatchString.Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        #endregion

        #region P/Invoke
        [DllImport("user32.dll", EntryPoint = "FindWindow")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        #endregion
    }
}
