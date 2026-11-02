# ZabbixMap

Aplicativo desktop (Windows, Linux, macOS) para navegar, construir e monitorar mapas do Zabbix, no estilo do The Dude da MikroTik. Feito em C#/.NET 10 com Avalonia.

Os mapas são os mapas nativos do Zabbix (`map.*`): não há banco próprio, e o que é salvo aqui aparece no frontend do Zabbix. Propriedades que o app não edita, como URLs e triggers de link, são preservadas.

## Executar

    dotnet run --project src/ZabbixMap.App

Para gerar executáveis que não exigem .NET: `./publish.sh` (saída em `dist/`).

## Uso

| Ação | Como |
| --- | --- |
| Abrir mapa | Duplo clique na árvore |
| Resumo do host | Duplo clique no host do mapa, ou botão direito no mapa ou na árvore |
| Mover a vista | Arrastar o fundo, botão do meio ou clicar no mini-mapa |
| Zoom | Roda do mouse; `F` ajusta à janela |
| Buscar host | `Ctrl+F`, `Enter` vai ao próximo; também filtra a árvore |
| Editar | Botão **Editar** destrava o mapa |
| Adicionar host | Arrastar da árvore para o mapa, ou **+ Host** |
| Criar link | **Ligar** e arrastar de um elemento a outro |
| Rx/Tx e estilo do link | Botão direito no link, **Editar link…** |
| Seleção múltipla | `Ctrl`+clique ou `Shift`+arrastar |
| Gravar | `Ctrl+S` |

## Resumo do host

Mostra os últimos dados do host agrupados pela etiqueta `component` (como o subfiltro de "Dados recentes" do Zabbix) e, dentro dela, por interface ou sistema de arquivos. Os botões de componente filtram a lista. Em servidores anteriores à 5.4 o agrupamento usa as applications. Clicar num item numérico mostra o gráfico do histórico (1 h, 6 h, 24 h ou 7 dias; 7 dias usa as médias horárias de trends); nas interfaces, recebido e enviado aparecem no mesmo gráfico. Os dados se atualizam a cada 30 s.

## Status e alertas

O estado é consultado a cada 15 s. Um host conta como **parado** quando todas as interfaces estão indisponíveis, ou quando há trigger de severidade High ou maior cujo nome indica queda (`unavailable`, `unreachable`, `down`, `indispon`, `inacess`). Ao parar, o ícone da bandeja fica vermelho, aparece um aviso no app e uma notificação do sistema. Fechar a janela mantém o monitoramento na bandeja; **Sair** no menu da bandeja encerra.

## Compatibilidade

O cliente lê `apiinfo.version` e adapta autenticação, formato dos elementos, disponibilidade de hosts e sintaxe de macros (`{?last(/host/chave)}` a partir da 5.4, `{host:chave.last()}` antes).

Rótulos de tráfego no frontend do próprio Zabbix aparecem como valor bruto; a formatação em `Gbps` é feita por este app.

A conexão fica guardada na máquina: o token de API, ou a sessão devolvida pelo login com senha, vai para o cofre do sistema (Secret Service/KWallet/GNOME Keyring no Linux, Keychain no macOS). No Windows, ou quando não há cofre disponível, vai para `secrets.json` na pasta de configuração do usuário, com permissão só do usuário; no Windows o valor também é cifrado com DPAPI. A senha nunca é salva. Se a sessão expirar no Zabbix, o app pede o login de novo.

## Testes

    dotnet test --project tests/ZabbixMap.Tests

`ZABBIXMAP_KEYRING_TEST=1` inclui o teste que grava e apaga uma credencial de teste no cofre real do sistema.

`dev/docker-compose.yml` sobe um Zabbix 7.0 local em `http://localhost:8088` (Admin / zabbix).
