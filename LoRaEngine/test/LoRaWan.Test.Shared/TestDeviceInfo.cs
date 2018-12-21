using System;
using System.Collections.Generic;

namespace LoRaWan.Test.Shared
{
    public class TestDeviceInfo
    {
        // Device ID in IoT Hub
        public string DeviceID { get; set; }

        // Indicates if the device actually exists in IoT Hub
        public bool IsIoTHubDevice { get; set; }

        // Application Identifier
        // Used by OTAA devices
        public string AppEUI { get; set; }

        

        // Application Key
        // Dynamically activated devices (OTAA) use the Application Key (AppKey) 
        // to derive the two session keys during the activation procedure
        public string AppKey { get; set; }

        // 32 bit device address (non-unique)
        // LoRaWAN devices have a 64 bit unique identifier that is assigned to the device
        // by the chip manufacturer, but communication uses 32 bit device address
        // In Over-the-Air Activation (OTAA) devices performs network join, where a DevAddr and security key are negotiated with the device. 
        public string DevAddr { get; set; }

        // Application Session Key
        // Used for encryption and decryption of the payload
        public string AppSKey { get; set; }

        // Network Session Key
        // Used for interaction between the device and Network Server. 
        // This key is used to check the validity of messages (MIC check)
        public string NwkSKey { get; set; }

        // Associated IoT Edge device
        public string GatewayID { get; set; }

        // Decoder used by the device
        // Project supports following values: DecoderGpsSensor, DecoderTemperatureSensor, DecoderValueSensor
        public string SensorDecoder { get; set; } = "DecoderValueSensor";

        /// <summary>
        /// Gets the desired properties for the <see cref="TestDeviceInfo"/>
        /// </summary>
        /// <returns></returns>
        public Dictionary<string, string> GetDesiredProperties()
        {
            var desiredProperties = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(this.AppEUI))
                desiredProperties[nameof(AppEUI)] = this.AppEUI;

            if (!string.IsNullOrEmpty(this.AppKey))
                desiredProperties[nameof(AppKey)] = this.AppKey;

            if (!string.IsNullOrEmpty(this.GatewayID))
                desiredProperties[nameof(GatewayID)] = this.GatewayID;

            if (!string.IsNullOrEmpty(this.SensorDecoder))
                desiredProperties[nameof(SensorDecoder)] = this.SensorDecoder;                
            
            if (!string.IsNullOrEmpty(this.AppSKey))
                desiredProperties[nameof(AppSKey)] = this.AppSKey;

            if (!string.IsNullOrEmpty(this.NwkSKey))
                desiredProperties[nameof(NwkSKey)] = this.NwkSKey; 

            if (!string.IsNullOrEmpty(this.DevAddr))
                desiredProperties[nameof(DevAddr)] = this.DevAddr;
            
            return desiredProperties;
        }


        /// <summary>
        /// Creates a <see cref="TestDeviceInfo"/> with ABP authentication
        /// </summary>
        /// <param name="deviceID">Device identifier. It will padded with 0's</param>
        /// <param name="prefix"></param>
        /// <param name="gatewayID"></param>
        /// <param name="sensorDecoder"></param>
        /// <returns></returns>
        public static TestDeviceInfo CreateABPDevice(UInt32 deviceID, string prefix = null, string gatewayID = null, string sensorDecoder = "DecoderValueSensor")
        {
            var padding8 =  "00000000";
            var padding16 = "0000000000000000";
            var padding32 = "00000000000000000000000000000000";

            if (!string.IsNullOrEmpty(prefix))
            {
                padding8 = string.Concat(prefix, padding8.Substring(prefix.Length));
                padding16 = string.Concat(prefix, padding16.Substring(prefix.Length));
                padding32 = string.Concat(prefix, padding32.Substring(prefix.Length));
            }

            var result = new TestDeviceInfo
            {
                DeviceID = deviceID.ToString(padding16),
                AppEUI = deviceID.ToString(padding16),
                GatewayID = gatewayID,
                SensorDecoder = sensorDecoder,
                AppSKey = deviceID.ToString(padding32),
                NwkSKey = deviceID.ToString(padding32),
                DevAddr = deviceID.ToString(padding8),
            };

            return result;
        }


        /// <summary>
        /// Creates a <see cref="TestDeviceInfo"/> with OTAA authentication
        /// </summary>
        /// <param name="deviceID">Device identifier. It will padded with 0's</param>
        /// <param name="prefix"></param>
        /// <param name="gatewayID"></param>
        /// <param name="sensorDecoder"></param>
        /// <returns></returns>
        public static TestDeviceInfo CreateOTAADevice(UInt32 deviceID, string prefix = null, string gatewayID = null, string sensorDecoder = "DecoderValueSensor")
        {
            var padding16 = "0000000000000000";
            var padding32 = "00000000000000000000000000000000";

            if (!string.IsNullOrEmpty(prefix))
            {
                padding16 = string.Concat(prefix, padding16.Substring(prefix.Length));
                padding32 = string.Concat(prefix, padding32.Substring(prefix.Length));
            }

            var result = new TestDeviceInfo
            {
                DeviceID = deviceID.ToString(padding16),
                AppEUI = padding16,
                AppKey = padding32,
                GatewayID = gatewayID,
                SensorDecoder = sensorDecoder,                
            };

            return result;
        }
    }
}