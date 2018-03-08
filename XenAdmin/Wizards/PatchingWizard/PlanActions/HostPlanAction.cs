﻿/* Copyright (c) Citrix Systems, Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using XenAdmin.Core;
using XenAPI;

namespace XenAdmin.Wizards.PatchingWizard.PlanActions
{
    public abstract class HostPlanAction : PlanActionWithSession
    {
        private readonly Host _currentHost;
        protected readonly XenRef<Host> HostXenRef;

        protected HostPlanAction(Host host, string description)
            : base(host.Connection, description)
        {
            HostXenRef = new XenRef<Host>(host);
        }

        protected override Host CurrentHost
        {
            get { return _currentHost; }
        }

        public Host GetResolvedHost()
        {
           return Connection.TryResolveWithTimeout(HostXenRef);
        }

        protected void EvacuateHost(ref Session session)
        {
            var hostObj = GetResolvedHost();

            Title = string.Format(Messages.PLANACTION_VMS_MIGRATING, hostObj.Name());
            PBD.CheckAndPlugPBDsFor(Connection.ResolveAll(hostObj.resident_VMs));

            log.DebugFormat("Disabling host {0}", hostObj.Name());
            Host.disable(session, HostXenRef.opaque_ref);

            Status = Messages.PLAN_ACTION_STATUS_MIGRATING_VMS_FROM_HOST;
            log.DebugFormat("Migrating VMs from host {0}", hostObj.Name());
            XenRef<Task> task = Host.async_evacuate(session, HostXenRef.opaque_ref);

            PollTaskForResultAndDestroy(Connection, ref session, task);
        }

        protected void BringBabiesBack(ref Session session, List<XenRef<VM>> vmrefs, bool enableOnly)
        {
            Status = Messages.PLAN_ACTION_STATUS_RECONNECTING_STORAGE;
            PBD.CheckAndBestEffortPlugPBDsFor(Connection, vmrefs);

            //
            // CA-17428: Apply hotfixes to a pool of hosts through XenCenter fails.
            //
            // Hosts do reenable themselves anyway, so just wait 1 min for that,  
            // occasionally poking it.
            //

            Status = Messages.PLAN_ACTION_STATUS_REENABLING_HOST;

            int retries = 0;
            while (!Host.get_enabled(session, HostXenRef.opaque_ref))
            {
                retries++;

                Thread.Sleep(5000);

                try
                {
                    Host.enable(session, HostXenRef.opaque_ref);
                }
                catch (Exception e)
                {
                    if (retries > 60)
                        throw;

                    log.Debug(string.Format("Cannot enable host {0}. Retrying in 5 sec.", HostXenRef.opaque_ref), e);
                }
            }

            if (enableOnly)
                return;

            int vmCount = vmrefs.Count;
            int vmNumber = 0;
            
            var hostObj = GetResolvedHost();

            foreach (VM vm in Connection.ResolveAll(vmrefs))
            {
                int tries = 0;

                if (vm.power_state != vm_power_state.Running)
                    continue; // vm may have been shutdown or suspended.

                do
                {
                    tries++;

                    try
                    {
                        Status = string.Format(Messages.PLAN_ACTION_STATUS_MIGRATING_VM_X_OF_Y, vmNumber + 1, vmCount);

                        log.DebugFormat("Migrating VM '{0}' back to Host '{1}'", vm.Name(), hostObj.Name());

                        PollTaskForResultAndDestroy(Connection, ref session,
                            VM.async_live_migrate(session, vm.opaque_ref, HostXenRef.opaque_ref),
                            (vmNumber * 100) / vmCount, ((vmNumber + 1) * 100) / vmCount);

                        vmNumber++;
                    }
                    catch (Failure e)
                    {
                        // When trying to put the first vm back, we get all sorts 
                        // of errors ie storage not plugged yet etc.  Just ignore them for now

                        if (vmNumber > 0 || tries > 24)
                            throw;

                        log.Debug(string.Format("Error migrating VM '{0}' back to Host '{1}'", vm.Name(), hostObj.Name()), e);

                        Thread.Sleep(5000);
                    }
                } while (vmNumber == 0);
            }

            log.DebugFormat("Cleaning up evacuated VMs from Host '{0}'", hostObj.Name());
            Host.ClearEvacuatedVMs(session, HostXenRef);
        }
    }
}