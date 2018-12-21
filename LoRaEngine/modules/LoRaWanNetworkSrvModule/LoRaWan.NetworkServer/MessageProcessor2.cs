﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using LoRaTools.LoRaMessage;
using LoRaTools.Regions;
using Microsoft.Azure.Devices.Client;
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LoRaWan.NetworkServer
{
    /// <summary>
    /// Message processor (work in progress)    
    /// </summary>
    /// <remarks>
    /// Refactor of current processor with the following goals in mind
    /// - Easier to understand and extend
    /// - Unit testable
    /// </remarks>
    public partial class MessageProcessor2
    {
        private readonly ILoRaDeviceRegistry deviceRegistry;
        private readonly ILoRaDeviceFrameCounterUpdateStrategy frameCounterUpdateStrategy;
        private readonly ILoRaPayloadDecoder payloadDecoder;

        public MessageProcessor2(
            ILoRaDeviceRegistry deviceRegistry,
            ILoRaDeviceFrameCounterUpdateStrategy frameCounterUpdateStrategy,
            ILoRaPayloadDecoder payloadDecoder)
        {
            this.deviceRegistry = deviceRegistry;
            this.frameCounterUpdateStrategy = frameCounterUpdateStrategy;
            this.payloadDecoder = payloadDecoder;
        }


        public class PhysicalPayload
        {
            public Rxpk[] GetRxpks() => null;
        }

        public class Rxpk
        {
            public LoRaPayload LoRaPayload { get; }

        }

        public class LoRaPayload
        {
            public string DevAddr { get; }
            public UInt16 NetId { get; }
            public UInt32 FcntUp { get; internal set; }

            public virtual bool CheckMic(string nwksKey)
            {
                return true;
            }

            public string GetDecryptedPayload(string appSKey)
            {
                return string.Empty;
            }
        }

        //Not a join message
        public class LoRaPayloadData : LoRaPayload
        {
            IEnumerable GetMacCommands() => null;

            public bool IsConfirmed() => false;
            
            public bool FPending { get; internal set; }

            internal bool IsUpwardAck()
            {
                throw new NotImplementedException();
            }
        }

        //downJoin
        public class LoRaPayloadJoinAccept : LoRaPayload
        {


        }

        //up join
        public class LoRaPayloadJoinRequest : LoRaPayload
        {


        }

        LoRaTools.LoRaMessage.LoRaPayloadData WorkaroundGetPayloadData(LoRaTools.LoRaPhysical.Rxpk rxpk)
        {
            byte[] convertedInputMessage = Convert.FromBase64String(rxpk.data);
            return new LoRaTools.LoRaMessage.LoRaPayloadData(convertedInputMessage);
        }

        byte WorkaroundGetNetID(LoRaTools.LoRaMessage.LoRaPayloadData loRaPayloadData)
        {
            return (byte)(loRaPayloadData.DevAddr.Span[0] & 0b01111111);
        }

        int WorkaroundGetFcnt(LoRaTools.LoRaMessage.LoRaPayloadData loRaPayloadData) => MemoryMarshal.Read<int>(loRaPayloadData.Fcnt.Span);

        LoRaMessageType WorkaroundGetMessageType(LoRaTools.LoRaMessage.LoRaPayloadData loRaPayloadData)
        {
            var messageType = loRaPayloadData.RawMessage[0] >> 5;
            return (LoRaMessageType)messageType;
        }

        bool WorkaroundIsConfirmed(LoRaTools.LoRaMessage.LoRaPayloadData loRaPayloadData) => WorkaroundGetMessageType(loRaPayloadData) == LoRaMessageType.ConfirmedDataUp;

        bool WorkaroundIsUpwardAck(LoRaTools.LoRaMessage.LoRaPayloadData loRaPayloadData) => WorkaroundGetMessageType(loRaPayloadData) == LoRaMessageType.ConfirmedDataUp && loRaPayloadData.GetLoRaMessage().Frmpayload.Length == 0;


        //public async Task<LoRaTools.LoRaPhysical.Txpk> ProcessLoRaMessage(Rxpk rxpk)
        public async Task<LoRaTools.LoRaPhysical.Txpk> ProcessLoRaMessage(LoRaTools.LoRaPhysical.Rxpk rxpk)
        {
            var timeWatcher = new LoRaOperationTimeWatcher(RegionFactory.CurrentRegion);

            

            var loraPayload = WorkaroundGetPayloadData(rxpk);
            var devAddr = loraPayload.DevAddr;
            var netId = loraPayload;
            if (!IsValidNetId(WorkaroundGetNetID(netId)))
            {
                //Log("Invalid netid");
                return null;
            }


            // Find device that matches:
            // - devAddr
            // - mic check (requires: loraDeviceInfo.NwkSKey or loraDeviceInfo.AppKey, rxpk.LoraPayload.Mic)
            // - gateway id
            var loraDeviceInfo = await deviceRegistry.GetDeviceForPayloadAsync(loraPayload);
            if (loraDeviceInfo == null)
                return null;

            // In order to handle a scenario where the network server is restarted and the fcntDown was not yet saved (we save every 10)
            // If device does not have gatewayid this will be handled by the service facade function (NextFCntDown)
            // here or at the deviceRegistry, what is better?
            if (loraDeviceInfo.IsABP() && loraDeviceInfo.GatewayID != null && loraDeviceInfo.WasNotJustReadFromCache())  
                loraDeviceInfo.IncrementFcntDown(10);


            // Reply attack or confirmed reply

            // Confirmed resubmit: A confirmed message that was received previously but we did not answer in time
            // Device will send it again and we just need to return an ack (but also check for C2D to send it over)
            var isConfirmedResubmit = false;
            if (this.WorkaroundGetFcnt(loraPayload) <= loraDeviceInfo.FcntUp)
            {
                // Future: Keep track of how many times we acked the confirmed message (4+ times we skip)
                //if it is confirmed most probably we did not ack in time before or device lost the ack packet so we should continue but not send the msg to iothub 
                if (WorkaroundIsConfirmed(loraPayload) && this.WorkaroundGetFcnt(loraPayload) == loraDeviceInfo.FcntUp)
                {
                    isConfirmedResubmit = true;
                }
                else
                {
                    return null;
                }
            }


            UInt32 fcntDown = 0;

            // Leaf devices that restart lose the counter. In relax mode we accept the incoming frame counter
            // ABP device does not reset the Fcnt so in relax mode we should reset for 0 (LMIC based) or 1
            if (loraDeviceInfo.IsABP() && loraDeviceInfo.IsABPRelaxedFrameCounter() && loraDeviceInfo.FcntUp > 0 && WorkaroundGetFcnt(loraPayload) <= 1)
            {
                // known problem when device restarts, starts fcnt from zero
                //loraDeviceInfo.SetFcntUp(0);
                //loraDeviceInfo.SetFcntDown(0);
                //_ = SaveFcnt(loraDeviceInfo, force: true);
                //if (loraDeviceInfo.GatewayID == null)
                //    await ABPFcntCacheReset(loraDeviceInfo);
                _ = frameCounterUpdateStrategy.ForceUpdateAsync(loraDeviceInfo, 0, 0);                
            }

            // If it is confirmed it require us to update the frame counter down
            // Multiple gateways: in redis, otherwise in device twin
            if (WorkaroundIsConfirmed(loraPayload))
            {
                fcntDown = await frameCounterUpdateStrategy.NextFcntDown(loraDeviceInfo);
                //fcntDown = NextFcntDown(loraDeviceInfo);
            }


            if (!isConfirmedResubmit)
            {
                var validFcntUp = WorkaroundGetFcnt(loraPayload) > loraDeviceInfo.FcntUp;
                if (validFcntUp)
                {
                    object payloadData = null;
                    // if it is an upward acknowledgement from the device it does not have a payload
                    // This is confirmation from leaf device that he received a C2D confirmed
                    if (!WorkaroundIsUpwardAck(loraPayload))
                    {
                        var decryptedPayload = loraPayload.GetDecryptedPayload(loraDeviceInfo.AppSKey);
                        payloadData = payloadDecoder.DecodeAsync(loraDeviceInfo, decryptedPayload);
                    }


                    // What do we need to send an UpAck to IoT Hub?
                    // What is the content of the message
                    // TODO Future: Don't wait if it is an unconfirmed message
                    await SendDeviceEventAsync(loraDeviceInfo, rxpk, payloadData);
                }
            }

            // We check if we have time to futher progress or not
            // C2D checks are quite expensive so if we are really late we just stop here
            var timeToSecondWindow = timeWatcher.GetTimeToSecondWindow(loraDeviceInfo);
            if (timeToSecondWindow < LoRaOperationTimeWatcher.ExpectedTimeToPackageAndSendMessage)
            {
                return null;
            }

            // If it is confirmed and we don't have time to check c2d and send to device we return now
            if (loraPayload.IsConfirmed() && timeToSecondWindow <= (LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessage + LoRaOperationTimeWatcher.ExpectedTimeToPackageAndSendMessage))
            {

                //_ = SaveFcnt(loraDeviceInfo, force: false);
                _ = this.frameCounterUpdateStrategy.UpdateAsync(loraDeviceInfo);
                return new LoRaTools.LoRaPhysical.Txpk()
                {                    
                };
            }

            // ReceiveAsync has a longer timeout
            // But we wait less that the timeout (available time before 2nd window)
            // if message is received after timeout, keep it in loraDeviceInfo and return the next call
            timeToSecondWindow = timeWatcher.GetTimeToSecondWindow(loraDeviceInfo);
            var c2dMsg = await loraDeviceInfo.ReceiveCloudToDeviceAsync(waitTime: timeToSecondWindow - LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessage);
            if (c2dMsg != null && !ValidateCloudToDeviceMessage(loraDeviceInfo, c2dMsg))
            {
                // complete message and set to null
                _ = loraDeviceInfo.CompleteCloudToDeviceMessageAsync(c2dMsg);
                c2dMsg = null;
            }

            var returnPayloadData = new LoRaPayloadData();
            //loraPayload.IsConfirmed() ? LoRaMessageType.ConfirmedDataDown : LoRaMessageType.UnconfirmedDataDown);

            if (c2dMsg != null)
            {
                // The message coming from the device was not confirmed, therefore we did not computed the frame count down
                // Now we need to increment because there is a C2D message to be sent
                if (!loraPayload.IsConfirmed())
                {
                    fcntDown = await this.frameCounterUpdateStrategy.NextFcntDown(loraDeviceInfo);
                }

                timeToSecondWindow = timeWatcher.GetTimeToSecondWindow(loraDeviceInfo);
                if (timeToSecondWindow > LoRaOperationTimeWatcher.ExpectedTimeToPackageAndSendMessage)
                {
                    var additionalMsg = await loraDeviceInfo.ReceiveCloudToDeviceAsync(waitTime: timeToSecondWindow - LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessage);
                    if (additionalMsg != null)
                    {
                        returnPayloadData.FPending = true;
                        _ = loraDeviceInfo.AbandonCloudToDeviceMessageAsync(additionalMsg);
                    }
                }

                // prepare message to device
                //returnPayloadData.SetData(c2dMsg.Body, loraDeviceInfo.DevAddr, loraDeviceInfo.AppSKey);
                //returnPayloadData.FportDown = (byte)(c2dMsg.Properties["fport"]);
                //if (c2dMsg.Properties["confirmed"] == "true")
                //    returnPayloadData.SetConfirmed();

            }


            // No C2D message and request was not confirmed, return nothing
            if (!loraPayload.IsConfirmed() && c2dMsg == null)
            {
                //await SaveFnct(loraDeviceInfo, force: false);
                await frameCounterUpdateStrategy.UpdateAsync(loraDeviceInfo);
                return null;
            }

            // We did it in the LoRaPayloadData constructor
            // we got here:
            // a) was a confirm request
            // b) we have a c2d message
            //if (rxpk.IsConfirmed())
            //    txpk.SetAsAcknoledgement();


            var downReceiveWindow = 1;
            if (!loraDeviceInfo.AlwaysUseSecondWindow && timeWatcher.InTimeForFirstWindow(loraDeviceInfo))
                downReceiveWindow = 1;
            else if (timeWatcher.InTimeForSecondWindow(loraDeviceInfo))
                downReceiveWindow = 2;
            else
            {
                // TODO: verify if we should call Abandon message
                return null;
            }

            if (c2dMsg != null)
                _ = loraDeviceInfo.CompleteCloudToDeviceMessageAsync(c2dMsg);

            _ = this.frameCounterUpdateStrategy.UpdateAsync(loraDeviceInfo);
            //_ = SaveFcnt(loraDeviceInfo, force: false);

            return new LoRaTools.LoRaPhysical.Txpk()
            {
               

            };
           // return Txpk.Create(downReceiveWindow, payloadToDevice, loraDeviceInfo.NwkSKey);
        }

        private bool ValidateCloudToDeviceMessage(ILoRaDevice loraDeviceInfo, Message cloudToDeviceMsg)
        {
            return true;
        }

        private Task SendDeviceEventAsync(ILoRaDevice loraDeviceInfo, Rxpk rxpk, object payloadData)
        {
            throw new NotImplementedException();
        }


        bool IsValidNetId(byte netid)
        {
            return true;
        }
    }
}