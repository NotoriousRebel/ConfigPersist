# ConfigPersist

Utilizes modifying machine.config for persistence through CLR hooking, after installing signed .NET assembly
onto Global Assembly Cache. 

### Note

For this technique to work you will need to generate a 
keyfile, you can use a tool called [sn](https://docs.microsoft.com/en-us/dotnet/framework/tools/sn-exe-strong-name-tool) which stands for strong name.
Place that keyfile and make sure it is called **key.snk** inside the Keyfile 
directory or you can place key.snk and the executable in the same directory.
<br/>
To learn more about this technique feel free to read this [post](https://secbytes.net/Configuring-our-Machine-for-Persistence).
