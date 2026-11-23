**GoodbyeDPI-Kurulum.exe** dosyasını indirip çalıştır, kurulum bitince açılan panelde **Başlat**'a bas.

**1.1.0'da yeni:** İş Bankası gibi bazı sitelerde çıkan `ERR_CONNECTION_RESET` düzeldi. GoodbyeDPI paketi 2. bayttan bölüyordu, bu TLS başlığını kesiyor ve bazı sunucular bağlantıyı sıfırlıyordu; artık site adının hemen önünden bölüyor. Panel yöntem ararken bankaları ve e-Devlet'i de test ediyor, onları bozan yöntemi seçmiyor. 1.0.0'dan güncelleyenlerde panel eski yöntemi bırakıp yenisini kendisi bulur.

- Şifreli DNS (DNS-over-HTTPS) içinde: ayrı bir DNSCrypt ya da DNS programı gerekmez.
- Yöntem otomatik bulunur: panel yöntemleri sırayla dener, Discord ve Roblox'u açanı seçer.
- Pencereyi kapatınca bildirim alanında kalır; koruma arka planda sürer.
- Kaldırmak için: Ayarlar > Uygulamalar > GoodbyeDPI, ya da paneldeki **Kaldır**.

Windows "Windows kişisel bilgisayarınızı korudu" uyarısı gösterirse: **Ek bilgi > Yine de çalıştır**. Dosya imzasız olduğu için bu uyarı çıkar. Bazı antivirüsler WinDivert sürücüsünü yanlışlıkla işaretleyebilir.

Gereksinim: 64 bit Windows 10 ya da 11.

`GoodbyeDPI-Panel.exe` kurulumsuz tek dosya sürümüdür: çalıştırınca kendini kurar.
