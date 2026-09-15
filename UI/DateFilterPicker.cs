using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace WCAE
{
    // A blank date means no limit; the calendar and keyboard stay native.
    public sealed class DateFilterPicker : DateTimePicker
    {
        DateTime? selectedDate;
        bool updating, calendarCancelled;
        public event EventHandler SelectedDateChanged;

        public DateFilterPicker()
        {
            Format=DateTimePickerFormat.Custom; CustomFormat=" ";
            ShowCheckBox=false; Width=118;
        }
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime? SelectedDate
        {
            get => selectedDate;
            set
            {
                var next=value?.Date;
                if(next==selectedDate)return;
                updating=true;
                try
                {
                    if(next.HasValue)Value=next.Value;
                    selectedDate=next; CustomFormat=next.HasValue?"yyyy-MM-dd":" ";
                }
                finally { updating=false; }
                SelectedDateChanged?.Invoke(this,EventArgs.Empty);
            }
        }
        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);
            if(!updating)SelectedDate=Value;
        }
        protected override void OnDropDown(EventArgs e) { calendarCancelled=false; base.OnDropDown(e); }
        protected override void OnCloseUp(EventArgs e)
        {
            base.OnCloseUp(e);
            if(!calendarCancelled)SelectedDate=Value;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Delete || e.KeyCode==Keys.Back)
            {
                SelectedDate=null; e.SuppressKeyPress=true; return;
            }
            if(e.KeyCode==Keys.Escape)calendarCancelled=true;
            if(e.KeyCode==Keys.Enter)SelectedDate=Value;
            base.OnKeyDown(e);
        }
    }
}
