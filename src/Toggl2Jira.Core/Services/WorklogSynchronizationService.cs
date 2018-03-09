﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnsureThat;
using Newtonsoft.Json;
using Toggl2Jira.Core.Model;
using Toggl2Jira.Core.Repositories;

namespace Toggl2Jira.Core.Services
{
    public class WorklogSynchronizationService: IWorklogSynchronizationService
    {
        private static T Clone<T>(T originalValue) where T : new()
        {
            if (originalValue == null)
            {
                return new T();
            }
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(originalValue));
        }
        
        private readonly IWorklogConverter _worklogConverter;
        private readonly ITempoWorklogRepository _tempoWorklogRepository;
        private readonly ITogglWorklogRepository _togglWorklogRepository;

        public WorklogSynchronizationService(IWorklogConverter worklogConverter, ITempoWorklogRepository tempoWorklogRepository, ITogglWorklogRepository togglWorklogRepository)
        {
            EnsureArg.IsNotNull(togglWorklogRepository, nameof(togglWorklogRepository));
            EnsureArg.IsNotNull(tempoWorklogRepository, nameof(tempoWorklogRepository));
            EnsureArg.IsNotNull(worklogConverter, nameof(worklogConverter));

            _worklogConverter = worklogConverter;
            _tempoWorklogRepository = tempoWorklogRepository;
            _togglWorklogRepository = togglWorklogRepository;                                    
        }

        public async Task<SynchronizationResult> SynchronizeAsync(Worklog worklog)
        {
            EnsureArg.IsNotNull(worklog);
            
            var tempoWorklogToSend = CreateTempoWorklogToSend(worklog);
            var togglWorklogToSend = CreateTogglWorklogToSend(worklog);
            try
            {
                var saveToTempoTask = TempoWorklogComparer.Instance.Equals(worklog.TempoWorklog, tempoWorklogToSend) == false
                    ? _tempoWorklogRepository.SaveWorklogsAsync(new[] { tempoWorklogToSend })
                    : Task.CompletedTask;
                var saveToTogglTask = TogglWorklogComparer.Instance.Equals(worklog.TogglWorklog, togglWorklogToSend) == false
                    ? _togglWorklogRepository.SaveWorklogsAsync(new[] { togglWorklogToSend })
                    : Task.CompletedTask;

                await Task.WhenAll(saveToTempoTask, saveToTogglTask);
                
                worklog.TogglWorklog = togglWorklogToSend;
                worklog.TempoWorklog = tempoWorklogToSend;
                
                return SynchronizationResult.CreateSuccess();
            }
            catch(Exception syncException) {
                try
                {
                    await RollbackSynchronizationAsync(worklog, togglWorklogToSend, tempoWorklogToSend);
                }
                catch (Exception rollbackException)
                {
                    return SynchronizationResult.CreateRollbackSynchronizationError(syncException, rollbackException);
                }

                return SynchronizationResult.CreateSynchronizationError(syncException);
            }
        }

        public async Task<WorklogsLoadResult> LoadAsync(DateTime startDate, DateTime endDate)
        {
            var togglTask = _togglWorklogRepository.GetWorklogsAsync(startDate, endDate);
            var tempoTask = _tempoWorklogRepository.GetWorklogsAsync(startDate, endDate);

            await Task.WhenAll(togglTask, tempoTask);
            var togglWorklogs = await togglTask;
            var tempoWorklogs = await tempoTask;

            var worklogs = togglWorklogs.Select(w => _worklogConverter.FromTogglWorklog(w)).ToArray();
            var notMatchedTempoWorklogs = new List<Worklog>();
            foreach (var tempoWorklog in tempoWorklogs)
            {
                var matchedWorklog = worklogs.FirstOrDefault(w => w.StartDate == tempoWorklog.dateStarted);
                if (matchedWorklog == null)
                {                    
                    notMatchedTempoWorklogs.Add(new Worklog() { TempoWorklog = tempoWorklog});
                }
                else
                {
                    matchedWorklog.TempoWorklog = tempoWorklog;
                }
            }

            return new WorklogsLoadResult() { NotMatchedWorklogs = notMatchedTempoWorklogs.ToArray(), Worklogs = worklogs };
        }

        public async Task DeleteAsync(Worklog worklog)
        {
            if(worklog.TempoWorklog != null)
            {
                await _tempoWorklogRepository.DeleteWorklogsAsync(new[] { worklog.TempoWorklog });
                worklog.TempoWorklog = null;
            }

            if(worklog.TogglWorklog != null)
            {
                await _togglWorklogRepository.DeleteWorklogsAsync(new[] { worklog.TogglWorklog });
                worklog.TogglWorklog = null;
            }
        }

        private DateTime TrimToMinutes(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, value.Kind);
        }

        private TogglWorklog CreateTogglWorklogToSend(Worklog worklog)
        {
            var togglWorklogToSend = Clone(worklog.TogglWorklog);
            _worklogConverter.UpdateTogglWorklog(togglWorklogToSend, worklog);
            return togglWorklogToSend;
        }

        private TempoWorklog CreateTempoWorklogToSend(Worklog worklog)
        {
            var tempoWorklogToSend = Clone(worklog.TempoWorklog);
            _worklogConverter.UpdateTempoWorklog(tempoWorklogToSend, worklog);
            return tempoWorklogToSend;
        }

        private async Task RollbackSynchronizationAsync(Worklog worklog, TogglWorklog sentTogglWorklog, TempoWorklog sentTempoWorklog)
        {
            if (worklog.TogglWorklog?.id != null)
            {                
                await _togglWorklogRepository.SaveWorklogsAsync(new[] {worklog.TogglWorklog});
            }
            else
            {                
                await _togglWorklogRepository.DeleteWorklogsAsync(new[] {sentTogglWorklog});
                worklog.TogglWorklog = null;
            }

            if (worklog.TempoWorklog?.id != null)
            {   
                await _tempoWorklogRepository.SaveWorklogsAsync(new[] {worklog.TempoWorklog});
            }
            else
            {
                await _tempoWorklogRepository.DeleteWorklogsAsync(new[] {sentTempoWorklog});
                worklog.TempoWorklog = null;
            }            
        }
    }
}