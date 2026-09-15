using System;
using System.Threading;
using System.Windows.Forms;

namespace WCAE
{
    /// <summary>Focus a running window, or wait for an already-closing instance to release its files.</summary>
    internal sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        readonly EventWaitHandle activate, closing;
        RegisteredWaitHandle activationWait;
        Form activeForm;
        bool ownsMutex;
        public bool IsOwner => ownsMutex;
        public SingleInstance(bool preview)
        {
            string suffix = preview ? "-Preview-" + Environment.ProcessId : "";
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WCAE-Activate" + suffix);
            closing = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\WCAE-Closing" + suffix);
            mutex = new Mutex(true, "Local\\WCAE" + suffix, out ownsMutex);
            if (!ownsMutex)
            {
                try
                {
                    if (closing.WaitOne(0)) ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15));
                    else ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) activate.Set();
            }
            if (ownsMutex) closing.Reset();
        }
        public void Attach(Form form)
        {
            ArgumentNullException.ThrowIfNull(form);
            activationWait?.Unregister(null);
            Volatile.Write(ref activeForm,form);
            if(form is MainForm main)
                main.FormClosing += (s,e) => { if(main.IsClosing) closing.Set(); };
            activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (state,timedOut) =>
            {
                try
                {
                    if (!ReferenceEquals(Volatile.Read(ref activeForm),form) || form.IsDisposed || !form.IsHandleCreated) return;
                    form.BeginInvoke(new Action(() =>
                    {
                        if(!ReferenceEquals(Volatile.Read(ref activeForm),form) || form.IsDisposed || form is MainForm current && current.IsClosing)return;
                        if(form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                        form.Show(); form.Activate();
                    }));
                }
                catch (InvalidOperationException) { }
            }, null, Timeout.Infinite, false);
        }
        public void Dispose()
        {
            Volatile.Write(ref activeForm,null);
            activationWait?.Unregister(null);
            if(ownsMutex) { ownsMutex=false; mutex.ReleaseMutex(); }
            mutex.Dispose(); activate.Dispose(); closing.Dispose();
        }
    }
}
