﻿using Android.App;
using Android.Bluetooth;
using Android.OS;
using Java.Lang;
using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Exception = System.Exception;


namespace Plugin.BluetoothLE.Internals
{
    public class DeviceContext
    {
        public DeviceContext(BluetoothDevice device, GattCallbacks callbacks)
        {
            this.NativeDevice = device;
            this.Callbacks = callbacks;
        }


        public BluetoothGatt Gatt { get; private set; }
        public BluetoothDevice NativeDevice { get; }
        public GattCallbacks Callbacks { get; }


        readonly AutoResetEvent reset = new AutoResetEvent(true);
        public IObservable<T> Lock<T>(IObservable<T> inner) => Observable.Create<T>(ob =>
        {
            Log.Debug("Device", "Lock - at the gate");
            this.reset.WaitOne();
            Log.Debug("Device", "Lock - past the gate");

            return inner.Subscribe(
                ob.OnNext,
                ob.OnError,
                ob.OnCompleted
            );
        })
        .Finally(() =>
        {
            Log.Debug("Device", "Releasing sync lock");
            this.reset.Set();
        });


        public IObservable<object> Marshall(Action action) => Observable.Create<object>(ob =>
        {
            if (CrossBleAdapter.AndroidPerformActionsOnMainThread)
            {
                Application.SynchronizationContext.Post(_ =>
                {
                    try
                    {
                        action();
                        ob.Respond(null);
                    }
                    catch (Exception ex)
                    {
                        ob.OnError(ex);
                    }
                }, null);
            }
            else
            {
                action();
                ob.Respond(null);
            }
            return Disposable.Empty;
        });


        public IObservable<object> Reconnect(ConnectionPriority priority)
        {
            if (this.Gatt == null)
                throw new ArgumentException("Device is not in a reconnectable state");

            return this.Marshall(() => this.Gatt.Connect());
        }


        public IObservable<object> Connect(ConnectionPriority priority, bool androidAutoReconnect) => this.Marshall(() =>
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.N || !androidAutoReconnect)
            {
                this.Gatt = this.ConnectGattCompat(androidAutoReconnect);
            }
            else
            {
                this.Gatt = this.CreateGatt(true);
            }

            if (priority != ConnectionPriority.Normal)
                this.Gatt.RequestConnectionPriority(this.ToNative(priority));
        });


        public void Close()
        {
            try
            {
                this.Gatt?.Close();
                this.Gatt = null;
            }
            catch (Exception ex)
            {
                Log.Warn("Device", "Unclean disconnect - " + ex);
            }
        }


        BluetoothGatt CreateGatt(bool autoConnect)
        {
            try
            {
                var bmMethod = BluetoothAdapter.DefaultAdapter.Class.GetDeclaredMethod("getBluetoothManager");
                bmMethod.Accessible = true;
                var bluetoothManager = bmMethod.Invoke(BluetoothAdapter.DefaultAdapter);

                var method = bluetoothManager.Class.GetDeclaredMethod("getBluetoothGatt");
                method.Accessible = true;
                var iBluetoothGatt = method.Invoke(bluetoothManager);

                if (iBluetoothGatt == null)
                {
                    Log.Debug("Device", "Unable to find getBluetoothGatt object");
                    return this.ConnectGattCompat(autoConnect);
                }

                var bluetoothGatt = this.CreateReflectionGatt(iBluetoothGatt);
                if (bluetoothGatt == null)
                {
                    Log.Info("Device", "Unable to create GATT object via reflection");
                    return this.ConnectGattCompat(autoConnect);
                }
                var connectSuccess = this.ConnectUsingReflection(bluetoothGatt, true);
                if (!connectSuccess)
                {
                    Log.Error("Device", "Unable to connect using reflection method");
                    bluetoothGatt.Close();
                }
                return bluetoothGatt;
            }
            catch (Exception ex)
            {
                Log.Info("Device", "Defaulting to gatt connect compatible method - " + ex);
                return this.ConnectGattCompat(autoConnect);
            }
        }


        bool ConnectUsingReflection(BluetoothGatt bluetoothGatt, bool autoConnect)
        {
            if (autoConnect)
            {
                var autoConnectField = bluetoothGatt.Class.GetDeclaredField("mAutoConnect");
                autoConnectField.Accessible = true;
                autoConnectField.SetBoolean(bluetoothGatt, true);
            }
            var connectMethod = bluetoothGatt.Class.GetDeclaredMethod(
                "connect",
                Java.Lang.Boolean.Type,
                Class.FromType(typeof(BluetoothGattCallback))
            );
            connectMethod.Accessible = true;
            var result = (bool)connectMethod.Invoke(bluetoothGatt, autoConnect, this.Callbacks);
            return result;
        }


        BluetoothGatt CreateReflectionGatt(Java.Lang.Object bluetoothGatt)
        {
            var ctor = Class
                .FromType(typeof(BluetoothGatt))
                .GetDeclaredConstructors()
                .FirstOrDefault();

            ctor.Accessible = true;
            var parms = ctor.GetParameterTypes();
            var args = new Java.Lang.Object[parms.Length];
            for (var i = 0; i < parms.Length; i++)
            {
                var @class = parms[i].CanonicalName.ToLower();
                switch (@class)
                {
                    case "int":
                    case "integer":
                        args[i] = (int)BluetoothTransports.Le;
                        break;

                    case "android.bluetooth.ibluetoothgatt":
                        args[i] = bluetoothGatt;
                        break;

                    case "android.bluetooth.bluetoothdevice":
                        args[i] = this.NativeDevice;
                        break;

                    default:
                        args[i] = Application.Context;
                        break;
                }
            }
            var instance = (BluetoothGatt)ctor.NewInstance(args);
            return instance;
        }

        BluetoothGatt ConnectGattCompat(bool autoConnect) => Build.VERSION.SdkInt >= BuildVersionCodes.M
            ? this.NativeDevice.ConnectGatt(Application.Context, autoConnect, this.Callbacks, BluetoothTransports.Le)
            : this.NativeDevice.ConnectGatt(Application.Context, autoConnect, this.Callbacks);


        GattConnectionPriority ToNative(ConnectionPriority priority)
        {
            switch (priority)
            {
                case ConnectionPriority.Low:
                    return GattConnectionPriority.LowPower;

                case ConnectionPriority.High:
                    return GattConnectionPriority.High;

                default:
                    return GattConnectionPriority.Balanced;
            }
        }
    }
}

