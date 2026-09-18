# Third-party licences

## RiptideNetworking

The `Riptide/` and `RiptideSteamTransport/` directories contain RiptideNetworking, used under The
MIT License. Every source file in them carries a header pointing at this file, which is why it
ships with the mod: the licence requires the notice to travel with the code.

Upstream: https://github.com/RiptideNetworking/Riptide

```
The MIT License (MIT)

Copyright (c) Tom Weiland

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Note that `RiptideSteamTransport/SteamBootstrap.cs` is this project's own code living in a vendor
directory, and carries no MIT banner. It is not covered by the above.
