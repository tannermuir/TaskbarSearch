using System.Windows.Forms;

namespace TaskbarCapsLockIndicator;

internal sealed class IndicatorApplicationContext : ApplicationContext
{
    private readonly CapsLockIndicatorForm _indicatorForm;
    private readonly GlobalCapsLockToggleListener _capsLockListener;

    public IndicatorApplicationContext()
    {
        _indicatorForm = new CapsLockIndicatorForm();
        _capsLockListener = new GlobalCapsLockToggleListener(ToggleIndicator);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _capsLockListener.Dispose();
            _indicatorForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ToggleIndicator()
    {
        if (_indicatorForm.IsDisposed)
        {
            return;
        }

        _indicatorForm.BeginInvoke(new MethodInvoker(_indicatorForm.ToggleIndicator));
    }
}
