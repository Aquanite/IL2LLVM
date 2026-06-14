Imports System
Imports IL2LLVM.Attributes

Namespace ILTest

    Public NotInheritable Class Program
        Private Sub New()
        End Sub

        <EntryPoint>
        Public Shared Function Main() As Integer
            For i As Integer = 0 To 4
                Native.ClearLCD()
            Next

            Native.HomeUp()
            Native.DrawStatusBar()

            For i As Byte = 0 To 2
                Console.WriteLine("Hello, VB!")
            Next

            While Native.GetCSC() = 0
            End While

            Return 0
        End Function
    End Class

    Public NotInheritable Class Native
        Private Sub New()
        End Sub

        <NativeCall("os_ClrLCD")>
        Public Shared Sub ClearLCD()
            Throw New NotImplementedException()
        End Sub

        <NativeCall("os_HomeUp")>
        Public Shared Sub HomeUp()
            Throw New NotImplementedException()
        End Sub

        <NativeCall("os_DrawStatusBar")>
        Public Shared Sub DrawStatusBar()
            Throw New NotImplementedException()
        End Sub

        <NativeCall("os_PutStrFull")>
        Public Shared Sub PutStringFull(str As String)
            Throw New NotImplementedException()
        End Sub

        <NativeCall("os_GetCSC")>
        Public Shared Function GetCSC() As Byte
            Throw New NotImplementedException()
        End Function

        <NativeCall("os_SetCursorPos")>
        Public Shared Function SetCursorPos(x As Byte, y As Byte) As Byte
            Throw New NotImplementedException()
        End Function
    End Class

    Public NotInheritable Class Plugs
        Private Sub New()
        End Sub

        Private Shared Row As Byte = 0

        <Plug("System.Void System.Console::WriteLine(System.String)")>
        Public Shared Sub CWriteLine(value As String)
            Native.SetCursorPos(Row, 0)
            Native.PutStringFull(value)
            Row += 1
        End Sub

        <Export("__runtime_overflow_occured")>
        Public Shared Sub OverflowOccured()
            While True
            End While
        End Sub
    End Class

End Namespace
