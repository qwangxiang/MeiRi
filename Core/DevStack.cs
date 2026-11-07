using System;
using System.Linq;
using System.Windows.Threading;
using MeiRi.Services;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --stack</c>): checks the z-order contract — the calendar behaves like a
/// normal window (a click raises it) and the floating cards stay above it no matter what was
/// clicked last. Z index 0 is the topmost window on the desktop, so a smaller number is higher.
/// </summary>
public static class DevStack
{
    public static void Run(CalendarHost host, Action done)
    {
        CalendarHost.LogTimings = true;
        var calendar = host.Windows.FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }

        var date = DateOnly.FromDateTime(DateTime.Today);
        int step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            step++;
            try
            {
                switch (step)
                {
                    case 1:
                        host.OpenDay(date, calendar, toggle: false);
                        Log.Write("stack#1 open day: " + host.DescribeLayout());
                        break;
                    case 2:
                        WindowEffects.ForceForeground(calendar);
                        WindowEffects.RaiseWithoutActivation(calendar);
                        Log.Write("stack#2 calendar clicked: " + host.DescribeLayout());
                        break;
                    case 3:
                        WindowEffects.ForceForeground(calendar);
                        WindowEffects.RaiseWithoutActivation(calendar);
                        Log.Write("stack#3 calendar clicked again: " + host.DescribeLayout());
                        break;
                    case 4:
                        host.CloseEditor();
                        host.OpenBacklog(calendar, toggle: false);
                        Log.Write("stack#4 backlog open: " + host.DescribeLayout());
                        break;
                    case 5:
                        WindowEffects.ForceForeground(calendar);
                        WindowEffects.RaiseWithoutActivation(calendar);
                        Log.Write("stack#5 calendar clicked: " + host.DescribeLayout());
                        break;
                    case 6:
                        host.CloseBacklog();
                        Log.Write("stack#6 closed: " + host.DescribeLayout());
                        // a monitor change throws the widgets away; the cards are owned by a calendar
                        // window, so they have to be dropped and recreated, not reused
                        host.ForceRebuild();
                        Log.Write("stack#7 rebuilt: " + host.DescribeLayout());
                        break;
                    case 7:
                        calendar = host.Windows.FirstOrDefault();
                        if (calendar is null)
                        {
                            Log.Write("stack#8 no calendar after rebuild");
                        }
                        else
                        {
                            host.OpenDay(date, calendar, toggle: false);
                            Log.Write("stack#8 reopened after rebuild: " + host.DescribeLayout());
                        }
                        break;
                    case 8:
                        // a visible card must be able to move to another monitor's calendar, which
                        // re-assigns its owner while it is already on screen
                        if (host.Windows.Count > 1)
                        {
                            host.OpenDay(date.AddDays(1), host.Windows[1], toggle: false);
                            Log.Write("stack#9 adopted by second calendar: " + host.DescribeLayout());
                        }
                        timer.Stop();
                        done();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("stack# failed: " + ex);
                timer.Stop();
                done();
            }
        };
        timer.Start();
    }
}
